using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelfService.Foundation;

public sealed class CredentialVault
{
    readonly CredentialVaultOptions options;
    readonly ICredentialKeyProvider keys;
    readonly TimeProvider clock;
    readonly string root;
    sealed record Envelope(int Schema, string KeyId, CredentialProfile Profile, string Nonce, string Ciphertext, string Tag);

    public CredentialVault(CredentialVaultOptions options, ICredentialKeyProvider keys, TimeProvider? clock = null)
    {
        if (options is null || keys is null || !CredentialRules.ProfileId(options.EncryptionKeyId) ||
            !Guid.TryParseExact(options.CustomerId, "D", out var customer) || customer == Guid.Empty ||
            !Guid.TryParseExact(options.CellId, "D", out var cell) || cell == Guid.Empty || string.IsNullOrWhiteSpace(options.RootPath))
            throw new CredentialException("credential-vault-configuration-invalid");
        this.options = options; this.keys = keys; this.clock = clock ?? TimeProvider.System;
        root = Path.GetFullPath(options.RootPath); CredentialRules.RejectLinks(root); Directory.CreateDirectory(root); CredentialRules.RejectLinks(root);
    }

    public async Task<CredentialProfile> PutAsync(CredentialScope scope, CredentialSecret material, DateTimeOffset expiresUtc,
        long expectedGeneration = 0, CancellationToken cancellationToken = default)
    {
        scope = Bound(scope); CredentialRules.Token(material.Text());
        if (expiresUtc <= clock.GetUtcNow().AddSeconds(30) || expiresUtc > clock.GetUtcNow().AddHours(24) || expectedGeneration < 0)
            throw new CredentialException("credential-lifetime-invalid");
        using var gate = await Gate(cancellationToken);
        var path = ProfilePath(scope.AuthProfileId); Envelope? existing = null;
        if (File.Exists(path))
        {
            (existing, var old) = await Read(path, cancellationToken); old.Dispose();
            if (!CredentialRules.SameIdentity(existing.Profile.Scope, scope)) throw new CredentialException("credential-profile-retarget-denied");
        }
        if ((existing?.Profile.Generation ?? 0) != expectedGeneration) throw new CredentialException("credential-generation-conflict");
        var profile = new CredentialProfile(scope, scope.Provider == PipelineRequestFactory.GitHub ? "github-installation-token" : "entra-access-token", expiresUtc, expectedGeneration + 1, true);
        await Write(path, profile, material, cancellationToken); return Detached(profile);
    }

    public async Task<CredentialProfile> RevokeAsync(string profileId, long expectedGeneration, CancellationToken cancellationToken = default)
    {
        using var gate = await Gate(cancellationToken);
        var path = ProfilePath(profileId); var (envelope, secret) = await Read(path, cancellationToken); using var material = secret;
        if (envelope.Profile.Generation != expectedGeneration) throw new CredentialException("credential-generation-conflict");
        var revoked = envelope.Profile with { Generation = expectedGeneration + 1, Enabled = false };
        await Write(path, revoked, material, cancellationToken); return Detached(revoked);
    }

    public async Task<CredentialProfile> GetProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        using var gate = await Gate(cancellationToken); var (envelope, material) = await Read(ProfilePath(profileId), cancellationToken);
        material.Dispose(); return Detached(envelope.Profile);
    }

    public async Task<CredentialLease> ResolveAsync(CredentialScope scope, CancellationToken cancellationToken = default)
    {
        scope = Bound(scope); using var gate = await Gate(cancellationToken);
        var (envelope, material) = await Read(ProfilePath(scope.AuthProfileId), cancellationToken);
        try
        {
            Require(envelope.Profile, scope);
            return new CredentialLease(Detached(envelope.Profile), material);
        }
        catch { material.Dispose(); throw; }
    }

    public async Task RevalidateAsync(CredentialScope scope, long generation, CancellationToken cancellationToken = default)
    {
        using var lease = await ResolveAsync(scope, cancellationToken);
        if (lease.Profile.Generation != generation) throw new CredentialException("credential-generation-changed");
    }

    CredentialScope Bound(CredentialScope scope)
    {
        scope = CredentialRules.Snapshot(scope);
        if (scope.CustomerId != options.CustomerId || scope.CellId != options.CellId) throw new CredentialException("credential-customer-binding");
        return scope;
    }
    void Require(CredentialProfile profile, CredentialScope scope)
    {
        if (!profile.Enabled || profile.Generation <= 0 || profile.ExpiresUtc <= clock.GetUtcNow().AddSeconds(15) ||
            CredentialRules.ScopeDigest(profile.Scope) != CredentialRules.ScopeDigest(scope))
            throw new CredentialException("credential-not-current-or-authorized");
    }
    static CredentialProfile Detached(CredentialProfile profile) => profile with { Scope = CredentialRules.Snapshot(profile.Scope) };
    string ProfilePath(string id)
    {
        if (!CredentialRules.ProfileId(id)) throw new CredentialException("credential-profile-invalid");
        var path = Path.Combine(root, CredentialRules.Hash(id) + ".credential.json"); CredentialRules.RejectLinks(path); return path;
    }

    async Task<FileStream> Gate(CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, ".vault.lock"); var until = clock.GetUtcNow().AddSeconds(10); var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested(); CredentialRules.RejectLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (++attempts < 100 && clock.GetUtcNow() < until) { await Task.Delay(20, cancellationToken); }
            catch (IOException) { throw new CredentialException("credential-vault-busy"); }
        }
    }
    async Task<(Envelope Envelope, CredentialSecret Secret)> Read(string path, CancellationToken cancellationToken)
    {
        CredentialRules.RejectLinks(path);
        if (!File.Exists(path)) throw new CredentialException("credential-profile-not-found");
        if (new FileInfo(path).Length > 131072) throw new CredentialException("credential-envelope-invalid");
        byte[]? clear = null; byte[]? keyBytes = null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken); using var doc = CredentialRules.Document(bytes);
            var envelope = doc.RootElement.Deserialize<Envelope>(CredentialRules.Json) ?? throw new JsonException();
            var profile = Detached(envelope.Profile); Bound(profile.Scope);
            if (envelope.Schema != 1 || !CredentialRules.ProfileId(envelope.KeyId) || profile.Generation <= 0 ||
                profile.Kind != (profile.Scope.Provider == PipelineRequestFactory.GitHub ? "github-installation-token" : "entra-access-token") ||
                Path.GetFileName(path) != CredentialRules.Hash(profile.Scope.AuthProfileId) + ".credential.json") throw new JsonException();
            using var key = await Key(envelope.KeyId, cancellationToken); keyBytes = key.CopyBytes();
            var nonce = Convert.FromBase64String(envelope.Nonce); var cipher = Convert.FromBase64String(envelope.Ciphertext); var tag = Convert.FromBase64String(envelope.Tag);
            if (nonce.Length != 12 || tag.Length != 16 || cipher.Length is < 1 or > 32768) throw new JsonException();
            clear = new byte[cipher.Length]; using var aes = new AesGcm(keyBytes, 16);
            aes.Decrypt(nonce, cipher, tag, clear, Aad(envelope.KeyId, profile));
            var value = Encoding.UTF8.GetString(clear); CredentialRules.Token(value);
            return (envelope with { Profile = profile }, CredentialSecret.FromUtf8(value));
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or ArgumentException or NullReferenceException)
        { throw new CredentialException("credential-envelope-integrity"); }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); if (keyBytes is not null) CryptographicOperations.ZeroMemory(keyBytes); }
    }
    async Task Write(string path, CredentialProfile profile, CredentialSecret material, CancellationToken cancellationToken)
    {
        var clear = material.CopyBytes(); byte[]? keyBytes = null; string? temporary = null;
        try
        {
            using var key = await Key(options.EncryptionKeyId, cancellationToken); keyBytes = key.CopyBytes();
            var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[clear.Length]; var tag = new byte[16];
            using var aes = new AesGcm(keyBytes, 16); aes.Encrypt(nonce, clear, cipher, tag, Aad(options.EncryptionKeyId, profile));
            var envelope = new Envelope(1, options.EncryptionKeyId, profile, Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag));
            temporary = Path.Combine(root, ".write-" + Guid.NewGuid().ToString("N")); CredentialRules.RejectLinks(temporary);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, envelope, CredentialRules.Json, cancellationToken); await file.FlushAsync(cancellationToken); file.Flush(flushToDisk: true);
            }
            CredentialRules.RejectLinks(path); cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: true); temporary = null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear); if (keyBytes is not null) CryptographicOperations.ZeroMemory(keyBytes);
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
        }
    }
    async ValueTask<CredentialEncryptionKey> Key(string keyId, CancellationToken cancellationToken)
    {
        try { return await keys.GetKeyAsync(options.CustomerId, options.CellId, keyId, cancellationToken) ?? throw new CredentialException("credential-key-unavailable"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new CredentialException("credential-key-unavailable"); }
    }
    static byte[] Aad(string keyId, CredentialProfile profile) => JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, keyId, profile }, CredentialRules.Json);
}
