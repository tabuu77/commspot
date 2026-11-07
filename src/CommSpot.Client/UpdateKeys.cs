namespace CommSpot.Client;

/// <summary>Update signing public keys (Ed25519, base64). Rotating: sign with Next, then ship a build that moves Next to Current.</summary>
internal static class UpdateKeys
{
    public const string Current = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    public const string Next = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
}
