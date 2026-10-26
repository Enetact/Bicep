namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    internal bool RequiresBoundDispatchSession => custody.IsBoundDispatchOnly;

    static void RequireGeneralProvisionProfile(CredentialVaultOptions options)
    {
        if (options.AccessProfile != CredentialVaultAccessProfile.General)
            throw new CredentialException("credential-bound-dispatch-explicit-provision-required");
    }

    // Immutable file pins validate the actual marker kinds and both binding copies. They do not read
    // profiles, key material, mutable rollback state or the exclusive dispatch-admission gates.
    internal void RequireGenericCredentialSourceUse(string code)
    {
        CredentialNamespaceMetadataPin? marker = null;
        CredentialNamespaceMetadataPin[]? pins = null;
        try
        {
            marker = custody.HoldMetadataFile(CredentialNamespaceCustody.MarkerName);
            pins = rollback?.HoldNamespaceMetadata();
            marker.AssertCurrent(); custody.ValidateMetadataIdentity(); rollback?.ValidateNamespaceMetadata();
            if (RequiresBoundDispatchSession) throw new CredentialException(code);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new CredentialException("credential-namespace-custody-unavailable"); }
        finally
        {
            if (pins is not null) foreach (var pin in pins) pin.Dispose();
            marker?.Dispose();
        }
    }

    internal void RequireRecoverySourceProfile(string operation) =>
        RequireGenericCredentialSourceUse("credential-recovery-" + operation + "-bound-dispatch-profile-unsupported");

    // Called under the ordinary vault read/admission gate. No nested vault/rollback guard is taken.
    void RequireCredentialReadSession(CredentialScope scope, FoundationCredentialUseSession? session)
    {
        custody.ValidateCurrent(); rollback?.ValidateNamespaceMetadata();
        if (!RequiresBoundDispatchSession) return;
        if (session is null) throw new CredentialException("credential-bound-dispatch-session-required");
        session.AssertCredentialReadFor(this, scope);
    }
}
