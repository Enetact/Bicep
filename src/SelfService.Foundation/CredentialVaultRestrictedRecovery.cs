using System.Security.Cryptography;

namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    public async Task<CredentialRestrictedRecoveryInventory> InspectRestrictedRecoveryDependenciesAsync(
        CredentialVaultNamespaceIdentity expectedNamespace, CancellationToken cancellationToken = default)
    {
        RequireRestrictedRecoveryNamespace(expectedNamespace);
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested(); using var source = await OpenScopedRecoverySourceAsync(expectedNamespace, token);
            await source.AuthenticateAsync(token); await source.AssertCurrentAsync(token);
            var observed = ScopedRecoveryNow(); token.ThrowIfCancellationRequested();
            return source.RestrictedInventory(observed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Restricted credential recovery inventory canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw RestrictedRecoveryFailure("timeout"); }
        catch (OperationCanceledException) { throw RestrictedRecoveryFailure("unavailable"); }
        catch (CredentialException error) { throw RestrictedRecoveryError(error); }
        catch (Exception) { throw RestrictedRecoveryFailure("unavailable"); }
    }

    public async Task<CredentialRestrictedRecoverySet> CaptureRestrictedSignedRecoverySetAsync(CredentialVaultNamespaceIdentity expectedNamespace,
        RSA signer, string signerKeyId, long generation, DateTimeOffset notBeforeUtc, DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default)
    {
        RequireRestrictedRecoveryNamespace(expectedNamespace);
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        CredentialRestrictedRecoverySet? result = null;
        try
        {
            token.ThrowIfCancellationRequested(); using var source = await OpenScopedRecoverySourceAsync(expectedNamespace, token);
            // The actual profile, namespace, whole-state Pending and filesystem checks precede signer/key access.
            using var publicKey = FoundationStore.ValidateBackupSigner(signer); var issued = ScopedRecoveryNow();
            CredentialRestrictedRecoverySets.RequireSignature(new(3, "credential-restricted-recovery-set", "FoundationBoundDispatchOnly", Guid.NewGuid().ToString("N"),
                options.CustomerId, options.CellId, expectedNamespace, generation, issued, issued, notBeforeUtc, expiresUtc,
                new string('a', 64), 0, 0, new string('b', 64), 2, signerKeyId,
                FoundationAuditAnchors.PublicKeyFingerprint(publicKey), "RSA-PSS-SHA256", ""), allowUnsigned: true);
            await source.AssertCurrentAsync(token); await source.AuthenticateAsync(token); await source.AssertCurrentAsync(token);
            var observed = ScopedRecoveryNow(); token.ThrowIfCancellationRequested(); var inventory = source.RestrictedInventory(observed);
            result = CredentialRestrictedRecoverySets.Create(inventory, source.Files, signer, publicKey, signerKeyId, generation, observed, notBeforeUtc, expiresUtc);
            await source.AssertCurrentAsync(token);
            var completed = ScopedRecoveryNow(); CredentialRestrictedRecoverySets.ValidateLifetime(result.Signature, completed); token.ThrowIfCancellationRequested();
            var captured = result; result = null; return captured;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Restricted credential recovery capture canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw RestrictedRecoveryFailure("timeout"); }
        catch (OperationCanceledException) { throw RestrictedRecoveryFailure("unavailable"); }
        catch (CredentialException error) { throw RestrictedRecoveryError(error); }
        catch (Exception) { throw RestrictedRecoveryFailure("unavailable"); }
        finally { result?.Dispose(); }
    }

    void RequireRestrictedRecoveryNamespace(CredentialVaultNamespaceIdentity expected)
    {
        RequireRestrictedRecoverySourceProfile();
        if (!ScopedRollbackFormat) throw RestrictedRecoveryFailure("scoped-format-required");
        CredentialRecoverySets.RequireIdentity(expected);
        if (expected.CustomerId != options.CustomerId || expected.CellId != options.CellId || expected.AuthorityNamespaceId is null)
            throw RestrictedRecoveryFailure("namespace-mismatch");
    }
    static CredentialException RestrictedRecoveryError(CredentialException error)
    {
        const string sharedPrefix = "credential-scoped-recovery-source-";
        return error.Code.StartsWith(sharedPrefix, StringComparison.Ordinal) ? RestrictedRecoveryFailure(error.Code[sharedPrefix.Length..]) : error;
    }
    static CredentialException RestrictedRecoveryFailure(string reason) => new("credential-restricted-recovery-source-" + reason);
}
