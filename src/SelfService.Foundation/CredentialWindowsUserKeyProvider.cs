using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SelfService.Foundation;

// A local Windows CurrentUser custody option. Dedicated production cells still need distinct OS/service identities.
public sealed class CredentialWindowsUserKeyProvider : ICredentialKeyProvider
{
    readonly string customerId, cellId, root;
    readonly CredentialNamespaceCustody custody;
    sealed record KeyEnvelope(int Schema, string CustomerId, string CellId, string KeyId, string ProtectedKey);

    public CredentialWindowsUserKeyProvider(string baseRootPath, string customerId, string cellId)
    {
        this.customerId = customerId; this.cellId = cellId;
        custody = new(NamespacePath(baseRootPath, customerId, cellId), "windows-user-keys", customerId, cellId); root = custody.Root;
    }

    public static CredentialWindowsUserKeyProvider ProvisionNamespace(string baseRootPath, string customerId, string cellId)
    {
        CredentialNamespaceCustody.Provision(NamespacePath(baseRootPath, customerId, cellId), "windows-user-keys", customerId, cellId);
        return new(baseRootPath, customerId, cellId);
    }

    static string NamespacePath(string baseRootPath, string customerId, string cellId)
    {
        if (!Guid.TryParseExact(customerId, "D", out var customer) || customer == Guid.Empty ||
            !Guid.TryParseExact(cellId, "D", out var cell) || cell == Guid.Empty)
            throw new CredentialException("credential-user-key-configuration-invalid");
        return Path.Combine(CredentialNamespaceCustody.LocalPath(baseRootPath), "cell-" + CredentialRules.Hash(customerId + ":" + cellId));
    }

    public ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken) =>
        AccessKeyAsync(customerId, cellId, keyId, provision: false, cancellationToken);

    // Explicit trusted-owner provisioning. Resolution and missing-key recovery never generate replacement material.
    public ValueTask<CredentialEncryptionKey> ProvisionKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken = default) =>
        AccessKeyAsync(customerId, cellId, keyId, provision: true, cancellationToken);

    async ValueTask<CredentialEncryptionKey> AccessKeyAsync(string customerId, string cellId, string keyId, bool provision, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows CurrentUser key custody is required.");
        if (customerId != this.customerId || cellId != this.cellId || !CredentialRules.ProfileId(keyId))
            throw new CredentialException("credential-user-key-binding-invalid");
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(root, CredentialRules.Hash(keyId) + ".dpapi.json"); CredentialRules.RejectLinks(path);
        using var gate = await Gate(cancellationToken);
        byte[]? clear = null;
        try
        {
            if (!File.Exists(path))
            {
                if (!provision) throw new CredentialException("credential-user-key-not-found");
                clear = RandomNumberGenerator.GetBytes(32);
                var protectedKey = Protect(clear, Entropy(keyId), encrypt: true);
                var envelope = new KeyEnvelope(1, customerId, cellId, keyId, Convert.ToBase64String(protectedKey));
                var temporary = Path.Combine(root, ".key-write-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await using (var file = custody.CreateNew(temporary))
                    {
                        await JsonSerializer.SerializeAsync(file, envelope, CredentialRules.Json, cancellationToken); await file.FlushAsync(cancellationToken); file.Flush(flushToDisk: true);
                    }
                    custody.ValidateCurrent(); custody.ValidateFile(temporary); CredentialRules.RejectLinks(path);
                    cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            else
            {
                if (provision) throw new CredentialException("credential-user-key-already-provisioned");
                using var doc = CredentialRules.Document(await custody.ReadFileAsync(path, 16384, cancellationToken));
                var envelope = doc.RootElement.Deserialize<KeyEnvelope>(CredentialRules.Json);
                if (envelope is null || envelope.Schema != 1 || envelope.CustomerId != customerId || envelope.CellId != cellId || envelope.KeyId != keyId)
                    throw new CredentialException("credential-user-key-integrity-invalid");
                clear = Protect(Convert.FromBase64String(envelope.ProtectedKey), Entropy(keyId), encrypt: false);
            }
            if (clear.Length != 32) throw new CredentialException("credential-user-key-integrity-invalid");
            custody.ValidateCurrent(); custody.ValidateFile(path);
            return new CredentialEncryptionKey(clear);
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or ArgumentException)
        { throw new CredentialException("credential-user-key-integrity-invalid"); }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }
    byte[] Entropy(string keyId) => SHA256.HashData(Encoding.UTF8.GetBytes("PlatformStudio.CredentialKey.v1\0" + customerId + "\0" + cellId + "\0" + keyId));
    async Task<FileStream> Gate(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return custody.OpenGuard(); }
            catch (IOException) when (attempt < 99) { await Task.Delay(20, cancellationToken); }
            catch (IOException) { throw new CredentialException("credential-user-key-busy"); }
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptProtectData(ref Blob data, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob result);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptUnprotectData(ref Blob data, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob result);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern IntPtr LocalFree(IntPtr memory);
    static byte[] Protect(byte[] value, byte[] entropy, bool encrypt)
    {
        var data = new Blob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        var extra = new Blob { Size = entropy.Length, Data = Marshal.AllocHGlobal(entropy.Length) }; Blob result = default;
        try
        {
            Marshal.Copy(value, 0, data.Data, value.Length); Marshal.Copy(entropy, 0, extra.Data, entropy.Length);
            var success = encrypt ? CryptProtectData(ref data, "Platform Studio customer credential key", ref extra, IntPtr.Zero, IntPtr.Zero, 1, out result) :
                CryptUnprotectData(ref data, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, 1, out result);
            if (!success || result.Size is <= 0 or > 16384 || result.Data == IntPtr.Zero) throw new CredentialException("credential-user-key-protection-failed");
            var bytes = new byte[result.Size]; Marshal.Copy(result.Data, bytes, 0, bytes.Length); return bytes;
        }
        finally
        {
            Marshal.Copy(new byte[value.Length], 0, data.Data, value.Length); Marshal.FreeHGlobal(data.Data);
            Marshal.Copy(new byte[entropy.Length], 0, extra.Data, entropy.Length); Marshal.FreeHGlobal(extra.Data);
            if (result.Data != IntPtr.Zero) { if (result.Size is > 0 and <= 16384) Marshal.Copy(new byte[result.Size], 0, result.Data, result.Size); _ = LocalFree(result.Data); }
        }
    }
}
