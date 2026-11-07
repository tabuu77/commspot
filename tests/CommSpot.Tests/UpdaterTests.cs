using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommSpot.Client.Core;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace CommSpot.Tests;

public sealed class UpdaterTests : IDisposable
{
    private readonly Ed25519PrivateKeyParameters _key = new(RandomNumberGenerator.GetBytes(32), 0);
    private readonly Ed25519PrivateKeyParameters _next = new(RandomNumberGenerator.GetBytes(32), 0);
    private readonly Ed25519PrivateKeyParameters _attacker = new(RandomNumberGenerator.GetBytes(32), 0);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "commspot-upd-" + Guid.NewGuid().ToString("N"));
    private static readonly Version Current = new(0, 2, 0);

    private byte[][] Trusted => [_key.GeneratePublicKey().GetEncoded(), _next.GeneratePublicKey().GetEncoded()];

    private static byte[] Sign(Ed25519PrivateKeyParameters k, byte[] data)
    {
        var s = new Ed25519Signer();
        s.Init(true, k);
        s.BlockUpdate(data, 0, data.Length);
        return s.GenerateSignature();
    }

    private static byte[] Manifest(string version, byte[] archive, string url, string product = Updater.Product) =>
        JsonSerializer.SerializeToUtf8Bytes(new UpdateManifest(product, version, new()
        {
            ["win-x64"] = new UpdateFile(url, Convert.ToHexStringLower(SHA256.HashData(archive)), archive.Length),
        }));

    private static byte[] Zip(params (string Name, string Body)[] files)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (n, b) in files)
            {
                using var w = new StreamWriter(z.CreateEntry(n).Open());
                w.Write(b);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void ValidNewerRelease_IsOffered_WithCurrentOrNextKey()
    {
        byte[] m = Manifest("0.3.0", [1, 2, 3], "https://github.com/x/y.zip");
        Assert.Equal(new Version(0, 3, 0), Updater.Evaluate(m, Sign(_key, m), Trusted, Current, "win-x64")!.Version);
        Assert.NotNull(Updater.Evaluate(m, Sign(_next, m), Trusted, Current, "win-x64"));
    }

    [Fact]
    public void SameOrOlderVersion_IsNotOffered()
    {
        foreach (var v in new[] { "0.2.0", "0.1.9" })
        {
            byte[] m = Manifest(v, [1], "https://github.com/x/y.zip");
            Assert.Null(Updater.Evaluate(m, Sign(_key, m), Trusted, Current, "win-x64"));
        }
    }

    [Fact]
    public void ForgedOrTamperedManifest_IsRejected()
    {
        byte[] m = Manifest("9.9.9", [1], "https://github.com/x/y.zip");
        Assert.Throws<InvalidDataException>(() => Updater.Evaluate(m, Sign(_attacker, m), Trusted, Current, "win-x64"));
        byte[] sig = Sign(_key, m);
        m[^2] ^= 1;
        Assert.Throws<InvalidDataException>(() => Updater.Evaluate(m, sig, Trusted, Current, "win-x64"));
        Assert.Throws<InvalidDataException>(() => Updater.Evaluate(m, [], Trusted, Current, "win-x64"));
        byte[] other = Manifest("9.9.9", [1], "https://github.com/x/y.zip", product: "something-else");
        Assert.Throws<InvalidDataException>(() => Updater.Evaluate(other, Sign(_key, other), Trusted, Current, "win-x64"));
    }

    private async Task<string> Download(byte[] archive, byte[]? served = null, string url = "https://github.com/x/CommSpot.zip", string exe = "CommSpot.exe")
    {
        byte[] m = Manifest("0.3.0", archive, url);
        var offer = Updater.Evaluate(m, Sign(_key, m), Trusted, Current, "win-x64")!;
        using var u = new Updater(Trusted, new Uri("https://github.com/x/manifest.json"), "win-x64", new Stub(served ?? archive));
        return await u.DownloadAsync(offer, _dir, exe);
    }

    [Fact]
    public async Task GoodZip_IsUnpacked()
    {
        string path = await Download(Zip(("CommSpot.exe", "new app")));
        Assert.Equal("new app", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task GoodTar_IsUnpacked()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true))
        using (var tw = new TarWriter(gz))
        {
            tw.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
            tw.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./CommSpot") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("linux app")) });
        }
        string path = await Download(ms.ToArray(), url: "https://github.com/x/CommSpot.tar.gz", exe: "CommSpot");
        Assert.Equal("linux app", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SwappedFile_FailsChecksum()
    {
        byte[] good = Zip(("CommSpot.exe", "new app"));
        byte[] evil = Zip(("CommSpot.exe", "evil app"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(good, served: evil));
    }

    [Fact]
    public async Task ExtraOrTraversalFiles_AreRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(Zip(("CommSpot.exe", "a"), ("../evil.dll", "b"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(Zip(("other.exe", "a"))));
    }

    [Fact]
    public async Task NonAllowedHost_IsRefused()
    {
        byte[] z = Zip(("CommSpot.exe", "a"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(z, url: "https://evil.example/CommSpot.zip"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(z, url: "http://github.com/x/CommSpot.zip"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private sealed class Stub(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
