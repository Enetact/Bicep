using System.Security.Cryptography;

namespace SelfService.Foundation;

// A concrete held read handle to one bounded immutable namespace document; never a caller-provided status or gate proof.
internal sealed class CredentialNamespaceMetadataPin : IDisposable
{
    readonly CredentialNamespaceCustody owner;
    readonly string path;
    readonly int length;
    readonly byte[] expectedSha256;
    FileStream? stream;
    internal CredentialNamespaceMetadataPin(CredentialNamespaceCustody owner, string path, FileStream stream, byte[] bytes)
    { this.owner = owner; this.path = path; this.stream = stream; length = bytes.Length; expectedSha256 = SHA256.HashData(bytes); }

    internal void AssertCurrent()
    {
        if (Volatile.Read(ref stream) is null) throw new CredentialException("credential-namespace-metadata-pin-disposed");
        owner.ValidateMetadataIdentity();
        var observed = owner.ReadFile(path, 16384);
        if (observed.Length != length || !CryptographicOperations.FixedTimeEquals(expectedSha256, SHA256.HashData(observed)))
            throw new CredentialException("credential-namespace-metadata-changed");
    }
    public void Dispose() => Interlocked.Exchange(ref stream, null)?.Dispose();
}
