using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace CommSpot.Client.Core;

public sealed record UpdateFile(string Url, string Sha256, long Size);

public sealed record UpdateManifest(string Product, string Version, Dictionary<string, UpdateFile> Files);

public sealed record UpdateOffer(Version Version, UpdateFile File);

/// <summary>
/// Self-update. The manifest is signed with Ed25519 by the release pipeline; the app pins the public keys
/// (current and next). Nothing is trusted until the signature checks out: not the host, not the file.
/// The download must then match the signed SHA-256 and size, and may only contain the app executable.
/// </summary>
public sealed class Updater : IDisposable
{
    public const string Product = "commspot-client";
    public const long MaxDownloadBytes = 300L * 1024 * 1024;

    /// <summary>Hosts updates may come from (GitHub releases and its download CDN). HTTPS only.</summary>
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly IReadOnlyList<byte[]> _keys;
    private readonly Uri _manifestUrl;
    private readonly string _rid;
    private readonly bool _allowAnyHost;

    /// <param name="allowAnyHost">Tests only: serve from a local server.</param>
    public Updater(IReadOnlyList<byte[]> trustedKeys, Uri manifestUrl, string rid, HttpMessageHandler? handler = null, bool allowAnyHost = false)
    {
        _keys = trustedKeys;
        _manifestUrl = manifestUrl;
        _rid = rid;
        _allowAnyHost = allowAnyHost;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CommSpot-Updater");
    }

    public static bool VerifySignature(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, IReadOnlyList<byte[]> keys)
    {
        if (signature.Length != 64)
        {
            return false;
        }
        byte[] d = data.ToArray();
        byte[] s = signature.ToArray();
        foreach (byte[] key in keys)
        {
            if (key.Length != 32)
            {
                continue;
            }
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
            verifier.BlockUpdate(d, 0, d.Length);
            if (verifier.VerifySignature(s))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Checks signature, product, platform and version. Returns an offer only for a newer, valid release.</summary>
    public static UpdateOffer? Evaluate(byte[] manifest, byte[] signature, IReadOnlyList<byte[]> keys, Version current, string rid)
    {
        if (!VerifySignature(manifest, signature, keys))
        {
            throw new InvalidDataException("Update manifest signature is invalid.");
        }
        var m = JsonSerializer.Deserialize<UpdateManifest>(manifest, Json) ?? throw new InvalidDataException("Empty manifest.");
        if (m.Product != Product || !Version.TryParse(m.Version, out var v))
        {
            throw new InvalidDataException("Manifest is not for this app.");
        }
        if (Normalize(v) <= Normalize(current) || m.Files is null || !m.Files.TryGetValue(rid, out var f))
        {
            return null; // no downgrades, no same-version reinstalls
        }
        if (f.Size <= 0 || f.Size > MaxDownloadBytes || f.Sha256.Length != 64 || !Uri.TryCreate(f.Url, UriKind.Absolute, out _))
        {
            throw new InvalidDataException("Manifest entry is malformed.");
        }
        return new UpdateOffer(v, f);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public async Task<UpdateOffer?> CheckAsync(Version current, CancellationToken ct = default)
    {
        byte[] manifest = await GetBytesAsync(_manifestUrl, 64 * 1024, ct).ConfigureAwait(false);
        byte[] sig = await GetBytesAsync(new Uri(_manifestUrl + ".sig"), 1024, ct).ConfigureAwait(false);
        return Evaluate(manifest, sig, _keys, current, _rid);
    }

    /// <summary>Downloads, verifies and unpacks the update. Returns the path of the new executable inside <paramref name="workDir"/>.</summary>
    public async Task<string> DownloadAsync(UpdateOffer offer, string workDir, string exeName, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        string archive = Path.Combine(workDir, "download.tmp");
        using (var resp = await SendAsync(new Uri(offer.File.Url), ct).ConfigureAwait(false))
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(archive))
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            byte[] buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > offer.File.Size)
                {
                    throw new InvalidDataException("Download is larger than the signed size.");
                }
                sha.AppendData(buf, 0, n);
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                progress?.Report((double)total / offer.File.Size);
            }
            string hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (total != offer.File.Size || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(hash), System.Text.Encoding.ASCII.GetBytes(offer.File.Sha256.ToLowerInvariant())))
            {
                throw new InvalidDataException("Download does not match the signed checksum.");
            }
        }

        string target = Path.Combine(workDir, exeName);
        File.Delete(target);
        if (offer.File.Url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            ExtractOnly(zip.Entries.Select(e => (e.FullName, (Func<Stream>)e.Open)), exeName, target);
        }
        else
        {
            await using var fs = File.OpenRead(archive);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            await using var tar = new TarReader(gz);
            TarEntry? e;
            bool found = false;
            while ((e = await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false)) is not null)
            {
                string name = e.Name.StartsWith("./", StringComparison.Ordinal) ? e.Name[2..] : e.Name;
                if (name.Length == 0 && e.EntryType == TarEntryType.Directory)
                {
                    continue;
                }
                if (name != exeName || e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || e.DataStream is null || found)
                {
                    throw new InvalidDataException($"Unexpected file in update: {e.Name}");
                }
                await using var o = File.Create(target);
                await e.DataStream.CopyToAsync(o, ct).ConfigureAwait(false);
                found = true;
            }
            if (!found)
            {
                throw new InvalidDataException("Update has no app executable.");
            }
        }
        File.Delete(archive);
        return target;
    }

    private static void ExtractOnly(IEnumerable<(string Name, Func<Stream> Open)> entries, string exeName, string target)
    {
        bool found = false;
        foreach (var (name, open) in entries)
        {
            if (name != exeName || found)
            {
                throw new InvalidDataException($"Unexpected file in update: {name}");
            }
            using var s = open();
            using var o = File.Create(target);
            s.CopyTo(o);
            found = true;
        }
        if (!found)
        {
            throw new InvalidDataException("Update has no app executable.");
        }
    }

    private async Task<byte[]> GetBytesAsync(Uri url, int max, CancellationToken ct)
    {
        using var resp = await SendAsync(url, ct).ConfigureAwait(false);
        if (resp.Content.Headers.ContentLength > max)
        {
            throw new InvalidDataException("Response too large.");
        }
        byte[] data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return data.Length <= max ? data : throw new InvalidDataException("Response too large.");
    }

    /// <summary>GET with redirects followed by hand, so every hop must be HTTPS on an allowed host.</summary>
    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken ct)
    {
        for (int hop = 0; hop < 5; hop++)
        {
            if (!_allowAnyHost && (url.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(url.Host)))
            {
                throw new InvalidDataException($"Refusing update host {url.Host}.");
            }
            var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                url = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                resp.Dispose();
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return resp;
        }
        throw new InvalidDataException("Too many redirects.");
    }

    public void Dispose() => _http.Dispose();
}
