namespace CommSpot.Client;

/// <summary>Update signing public keys (Ed25519, base64). Rotating: sign with Next, then ship a build that moves Next to Current.</summary>
internal static class UpdateKeys
{
    public const string Current = "Qc9PlJqvkBr+s9S1wJIMxRfKNK4HZCNPyK/FCbJ39Uk=";
    public const string Next = "XgFPVC9isLuMyiZ5H/0I8XkpgQN50xmJRGopNOcsVho=";
}
