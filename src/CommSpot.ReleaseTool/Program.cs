using System.Security.Cryptography;
using System.Text.Json;
using CommSpot.Client.Core;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

// Release helper used by .github/workflows/release.yml.
//   keygen                                  print a new key pair (private seed + public key, base64)
//   manifest <version> <distDir> <baseUrl>  write <distDir>/manifest.json for the client downloads
//   sign <file>                             write <file>.sig using the key in COMMSPOT_SIGNING_KEY
//   pubkey                                  print the public key for COMMSPOT_SIGNING_KEY
// COMMSPOT_SIGNING_KEY is a base64 32-byte seed or an OpenSSL Ed25519 private key PEM.

switch (args.FirstOrDefault())
{
    case "keygen":
    {
        byte[] seed = RandomNumberGenerator.GetBytes(32);
        var k = new Ed25519PrivateKeyParameters(seed, 0);
        Console.WriteLine("private: " + Convert.ToBase64String(seed));
        Console.WriteLine("public:  " + Convert.ToBase64String(k.GeneratePublicKey().GetEncoded()));
        return 0;
    }
    case "manifest" when args.Length == 4:
    {
        string version = args[1].TrimStart('v'), dist = args[2], baseUrl = args[3].TrimEnd('/');
        var files = new Dictionary<string, UpdateFile>();
        foreach (var (rid, name) in new[] { ("win-x64", "CommSpot-windows-x64.zip"), ("linux-x64", "CommSpot-linux-x64.tar.gz") })
        {
            string path = Path.Combine(dist, name);
            byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
            files[rid] = new UpdateFile($"{baseUrl}/{name}", Convert.ToHexStringLower(hash), new FileInfo(path).Length);
        }
        var m = new UpdateManifest(Updater.Product, version, files);
#pragma warning disable CA1869 // runs once per release
        File.WriteAllText(Path.Combine(dist, "manifest.json"),
            JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
#pragma warning restore CA1869
        return 0;
    }
    case "sign" when args.Length == 2:
    {
        byte[] data = File.ReadAllBytes(args[1]);
        var signer = new Ed25519Signer();
        var key = LoadKey();
        signer.Init(true, key);
        signer.BlockUpdate(data, 0, data.Length);
        byte[] sig = signer.GenerateSignature();
        if (!Updater.VerifySignature(data, sig, [key.GeneratePublicKey().GetEncoded()]))
        {
            Console.Error.WriteLine("Self-check failed.");
            return 1;
        }
        File.WriteAllBytes(args[1] + ".sig", sig);
        Console.WriteLine("Signed with key " + Convert.ToBase64String(key.GeneratePublicKey().GetEncoded()));
        return 0;
    }
    case "pubkey":
        Console.WriteLine(Convert.ToBase64String(LoadKey().GeneratePublicKey().GetEncoded()));
        return 0;
    default:
        Console.Error.WriteLine("usage: keygen | manifest <version> <distDir> <baseUrl> | sign <file> | pubkey");
        return 2;
}

static Ed25519PrivateKeyParameters LoadKey()
{
    string raw = (Environment.GetEnvironmentVariable("COMMSPOT_SIGNING_KEY") ?? "").Trim();
    if (raw.Length == 0)
    {
        throw new InvalidOperationException("COMMSPOT_SIGNING_KEY is not set.");
    }
    if (raw.StartsWith("-----BEGIN", StringComparison.Ordinal))
    {
        string b64 = string.Concat(raw.Split('\n').Where(l => !l.StartsWith("-----", StringComparison.Ordinal)).Select(l => l.Trim()));
        return (Ed25519PrivateKeyParameters)PrivateKeyFactory.CreateKey(Convert.FromBase64String(b64));
    }
    byte[] seed = Convert.FromBase64String(raw);
    return seed.Length == 32 ? new Ed25519PrivateKeyParameters(seed, 0) : throw new InvalidOperationException("Key must be 32 bytes.");
}
