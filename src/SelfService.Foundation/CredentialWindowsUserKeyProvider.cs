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
    sealed record Binding(int Schema, string CustomerId, string CellId);
    sealed record KeyEnvelope(int Schema, string CustomerId, string CellId, string KeyId, string ProtectedKey);

    public CredentialWindowsUserKeyProvider(string baseRootPath, string customerId, string cellId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows CurrentUser key custody is required.");
        if (!Guid.TryParseExact(customerId, "D", out var customer) || customer == Guid.Empty ||
            !Guid.TryParseExact(cellId, "D", out var cell) || cell == Guid.Empty || string.IsNullOrWhiteSpace(baseRootPath))
            throw new CredentialException("credential-user-key-configuration-invalid");
        this.customerId = customerId; this.cellId = cellId;
        root = Path.Combine(Path.GetFullPath(baseRootPath), "cell-" + CredentialRules.Hash(customerId + ":" + cellId));
        CredentialRules.RejectLinks(root);
        var existed = Directory.Exists(root); Directory.CreateDirectory(root); CredentialRules.RejectLinks(root);
        var marker = Path.Combine(root, "cell-binding.json"); CredentialRules.RejectLinks(marker);
        if (!File.Exists(marker))
        {
            if (existed && Directory.EnumerateFileSystemEntries(root).Any()) throw new CredentialException("credential-user-key-unowned-root");
            try
            {
                using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, new Binding(1, customerId, cellId), CredentialRules.Json); file.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(marker)) { }
        }
        if (new FileInfo(marker).Length > 1024) throw new CredentialException("credential-user-key-binding-invalid");
        try
        {
            using var doc = CredentialRules.Document(File.ReadAllBytes(marker)); var binding = doc.RootElement.Deserialize<Binding>(CredentialRules.Json);
            if (binding is null || binding.Schema != 1 || binding.CustomerId != customerId || binding.CellId != cellId)
                throw new CredentialException("credential-user-key-binding-invalid");
        }
        catch (JsonException) { throw new CredentialException("credential-user-key-binding-invalid"); }
        SecureDirectory(root);
    }

    public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
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
                clear = RandomNumberGenerator.GetBytes(32);
                var protectedKey = Protect(clear, Entropy(keyId), encrypt: true);
                var envelope = new KeyEnvelope(1, customerId, cellId, keyId, Convert.ToBase64String(protectedKey));
                var temporary = Path.Combine(root, ".key-write-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        await JsonSerializer.SerializeAsync(file, envelope, CredentialRules.Json, cancellationToken); await file.FlushAsync(cancellationToken); file.Flush(flushToDisk: true);
                    }
                    CredentialRules.RejectLinks(path); cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            else
            {
                if (new FileInfo(path).Length > 16384) throw new CredentialException("credential-user-key-integrity-invalid");
                using var doc = CredentialRules.Document(await File.ReadAllBytesAsync(path, cancellationToken));
                var envelope = doc.RootElement.Deserialize<KeyEnvelope>(CredentialRules.Json);
                if (envelope is null || envelope.Schema != 1 || envelope.CustomerId != customerId || envelope.CellId != cellId || envelope.KeyId != keyId)
                    throw new CredentialException("credential-user-key-integrity-invalid");
                clear = Protect(Convert.FromBase64String(envelope.ProtectedKey), Entropy(keyId), encrypt: false);
            }
            if (clear.Length != 32) throw new CredentialException("credential-user-key-integrity-invalid");
            return new CredentialEncryptionKey(clear);
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or ArgumentException)
        { throw new CredentialException("credential-user-key-integrity-invalid"); }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }
    byte[] Entropy(string keyId) => SHA256.HashData(Encoding.UTF8.GetBytes("PlatformStudio.CredentialKey.v1\0" + customerId + "\0" + cellId + "\0" + keyId));
    async Task<FileStream> Gate(CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, ".keys.lock");
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested(); CredentialRules.RejectLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 99) { await Task.Delay(20, cancellationToken); }
            catch (IOException) { throw new CredentialException("credential-user-key-busy"); }
        }
    }
    [SupportedOSPlatform("windows")]
    static void SecureDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new CredentialException("credential-user-identity-unavailable");
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
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
