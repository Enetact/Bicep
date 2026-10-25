using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    // This is the same AES/AAD authentication used for ordinary reads; no path lookup, provision or mutation is possible.
    internal static async Task<CredentialRecoveryProfileDependency> AuthenticateRecoveryEnvelopeAsync(ReadOnlyMemory<byte> encryptedBytes,
        string expectedProfileIdSha256, CredentialVaultNamespaceIdentity expectedNamespace, ICredentialKeyProvider keyProvider,
        CancellationToken cancellationToken)
    {
        CredentialRecoverySets.RequireIdentity(expectedNamespace);
        if (encryptedBytes.Length is < 2 or > 131072 || !CredentialRecoverySets.DigestShape(expectedProfileIdSha256) || keyProvider is null)
            throw CredentialRecoverySets.Failure("contents-invalid");
        var authenticated = await AuthenticateEnvelopeBytes(encryptedBytes, expectedProfileIdSha256, expectedNamespace.CustomerId,
            expectedNamespace.CellId, keyProvider, cancellationToken);
        using var secret = authenticated.Secret; cancellationToken.ThrowIfCancellationRequested();
        var profile = authenticated.Envelope.Profile; var scope = profile.Scope;
        var result = new CredentialRecoveryProfileDependency(expectedProfileIdSha256, CredentialRules.ScopeDigest(scope), scope.ProjectId,
            scope.EnvironmentId, profile.Generation, profile.Enabled, profile.ExpiresUtc.ToUnixTimeMilliseconds(), authenticated.Envelope.KeyId,
            CredentialRecoverySets.Hash(encryptedBytes.Span), encryptedBytes.Length);
        CredentialRecoveryInventory.ValidateProfile(result); return result;
    }

    static async Task<(Envelope Envelope, CredentialSecret Secret)> AuthenticateEnvelopeBytes(ReadOnlyMemory<byte> bytes,
        string expectedProfileHash, string customerId, string cellId, ICredentialKeyProvider keyProvider, CancellationToken cancellationToken)
    {
        byte[]? clear = null; byte[]? keyBytes = null;
        try
        {
            using var doc = CredentialRules.Document(bytes);
            var envelope = doc.RootElement.Deserialize<Envelope>(CredentialRules.Json) ?? throw new JsonException();
            var profile = Detached(envelope.Profile);
            if (profile.Scope.CustomerId != customerId || profile.Scope.CellId != cellId) throw new CredentialException("credential-customer-binding");
            if (envelope.Schema != 1 || !CredentialRules.ProfileId(envelope.KeyId) || profile.Generation <= 0 ||
                profile.Kind != (profile.Scope.Provider == PipelineRequestFactory.GitHub ? "github-installation-token" : "entra-access-token") ||
                expectedProfileHash != CredentialRules.Hash(profile.Scope.AuthProfileId)) throw new JsonException();
            CredentialEncryptionKey key;
            try { key = await keyProvider.GetKeyAsync(customerId, cellId, envelope.KeyId, cancellationToken) ?? throw new CredentialException("credential-key-unavailable"); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new CredentialException("credential-key-unavailable"); }
            using (key) { keyBytes = key.CopyBytes(); }
            cancellationToken.ThrowIfCancellationRequested();
            var nonce = Convert.FromBase64String(envelope.Nonce); var cipher = Convert.FromBase64String(envelope.Ciphertext); var tag = Convert.FromBase64String(envelope.Tag);
            if (nonce.Length != 12 || tag.Length != 16 || cipher.Length is < 1 or > 32768) throw new JsonException();
            clear = new byte[cipher.Length]; using var aes = new AesGcm(keyBytes, 16);
            aes.Decrypt(nonce, cipher, tag, clear, Aad(envelope.KeyId, profile));
            var value = Encoding.UTF8.GetString(clear); CredentialRules.Token(value);
            cancellationToken.ThrowIfCancellationRequested();
            return (envelope with { Profile = profile }, CredentialSecret.FromUtf8(value));
        }
        catch (Exception error) when (error is JsonException or FormatException or CryptographicException or ArgumentException or NullReferenceException)
        { throw new CredentialException("credential-envelope-integrity"); }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
            if (keyBytes is not null) CryptographicOperations.ZeroMemory(keyBytes);
        }
    }
}
