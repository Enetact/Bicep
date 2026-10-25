using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation.Owner;

public static class FoundationOwnerCli
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 8 };
    const string Marker = ".foundation-owner-initialization.json";
    sealed class Rejection(string code) : Exception { public string Code { get; } = code; }
    sealed record Inspect(string Operation, string CustomerId, string CellId, string DatabasePath);
    sealed record Initialize(string Operation, string CustomerId, string CellId, string DestinationDirectory,
        string AdministratorActorKey, string ReviewerActorKey, string ProjectId, string EnvironmentId);
    sealed record Authority(string Operation, string CustomerId, string CellId, string AuthorityDirectory);
    sealed record Activate(string Operation, string CustomerId, string CellId, string AuthorityDirectory,
        string DatabasePath, string DatabaseInstanceId, long ExpectedGeneration, string? ExpectedBindingSha256);
    sealed record Identity(string Operation, string CustomerId, string CellId, EntraCellOnboardingRequest IdentityContract);
    sealed record Backup(string Operation, string CustomerId, string CellId, string SnapshotPath, string ManifestPath,
        string PinnedPublicKeyPem, string PinnedPublicKeySha256, FoundationBackupRestoreAuthority RestoreAuthority,
        string? DestinationDirectory = null);

    public static int Run(string[] args)
    {
        var operation = "Rejected"; FileStream? output = null;
        try
        {
            if (args.Length != 4 || args[0] != "--request" || args[2] != "--output") throw Reject("OwnerArgumentsInvalid");
            var input = PathPolicy(args[1], existingFile: true);
            var resultPath = PathPolicy(args[3]);
            if (!Directory.Exists(Path.GetDirectoryName(resultPath))) throw Reject("OwnerResultParentMissing");
            if (File.Exists(resultPath) || Directory.Exists(resultPath)) throw Reject("OwnerResultAlreadyExists");
            if (!OperatingSystem.IsWindows()) throw Reject("OwnerWindowsQualificationRequired");
            OwnerFileProtection.AssertParent(resultPath, "OwnerResultParentCustodyRejected");
            using var requestInput = OwnerFileProtection.OpenRequest(input);
            var bytes = ReadBounded(requestInput, 65536);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            Unique(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("operation", out var action) ||
                action.ValueKind != JsonValueKind.String) throw Reject("OwnerRequestInvalid");
            var selected = action.GetString();
            if (selected is not ("inspect" or "initialize" or "authority.provision" or "authority.status" or "authority.activate" or "backup.verify" or "backup.restore" or "identity.propose"))
                throw Reject("OwnerOperationUnsupported");
            operation = selected;
            // Parse every allowed field before reserving the result or touching a mutation destination.
            object request = selected switch
            {
                "inspect" => Parse<Inspect>(document.RootElement), "initialize" => Parse<Initialize>(document.RootElement),
                "authority.provision" or "authority.status" => Parse<Authority>(document.RootElement),
                "authority.activate" => Parse<Activate>(document.RootElement), "identity.propose" => Parse<Identity>(document.RootElement), _ => Parse<Backup>(document.RootElement)
            };
            ValidateResultNamespace(request, resultPath);
            output = OwnerFileProtection.CreateResult(resultPath);
            var result = Execute(request);
            WriteResult(output, new { schema = "platform-studio.foundation-owner-result.v1", operation, state = "Succeeded", result,
                externalExecutionAuthorized = false, productionReady = false });
            Console.WriteLine(JsonSerializer.Serialize(new { schema = "platform-studio.foundation-owner-command.v1", operation,
                state = "Succeeded", code = "OwnerCommandCompleted", externalExecutionAuthorized = false }, Json));
            return 0;
        }
        catch (Exception error)
        {
            var code = error switch
            { Rejection own => own.Code, FoundationStoreException store => store.Code, EntraOnboardingException => "OwnerIdentityContractRejected", JsonException => "OwnerRequestInvalid",
                CryptographicException => "OwnerPublicKeyRejected", UnauthorizedAccessException => "OwnerAccessDenied",
                IOException => "OwnerIoRejected", SqliteException => "OwnerCellRejected", ArgumentException => "OwnerInputRejected", _ => "OwnerCommandFailed" };
            if (!Regex.IsMatch(code, "\\A[A-Za-z0-9._-]{1,128}\\z")) code = "OwnerCommandFailed";
            if (output is not null)
                try { output.SetLength(0); WriteResult(output, new { schema = "platform-studio.foundation-owner-result.v1", operation,
                    state = "Rejected", code, externalExecutionAuthorized = false, productionReady = false }); } catch { }
            Console.WriteLine(JsonSerializer.Serialize(new { schema = "platform-studio.foundation-owner-command.v1", operation,
                state = "Rejected", code, externalExecutionAuthorized = false }, Json));
            return 2;
        }
        finally { output?.Dispose(); }
    }

    static object Execute(object request) => request switch
    {
        Inspect inspect => InspectCell(inspect.DatabasePath, inspect.CustomerId, inspect.CellId),
        Initialize initialize => InitializeCell(initialize),
        Authority authority => AuthorityCommand(authority),
        Activate activate => ActivateCell(activate),
        Identity identity => IdentityProposal(identity),
        Backup backup => BackupCommand(backup),
        _ => throw Reject("OwnerOperationUnsupported")
    };

    static object InitializeCell(Initialize request)
    {
        Scope(request.CustomerId, request.CellId); Scope(request.ProjectId, request.EnvironmentId);
        Human(request.AdministratorActorKey); Human(request.ReviewerActorKey);
        if (request.AdministratorActorKey == request.ReviewerActorKey) throw Reject("OwnerDistinctReviewerRequired");
        var directory = PathPolicy(request.DestinationDirectory);
        if (!Directory.Exists(Path.GetDirectoryName(directory))) throw Reject("OwnerDestinationParentMissing");
        if (File.Exists(directory) || Directory.Exists(directory)) throw Reject("OwnerDestinationAlreadyExists");
        RejectAuthorityNamespace(directory);
        if (!OperatingSystem.IsWindows()) throw Reject("OwnerInitializationWindowsQualificationRequired");
        if (!OwnerFileProtection.CreateDirectory(directory)) throw Reject("OwnerDestinationReservationRejected");
        // This fresh directory belongs to this command. Failure retains evidence; existing directories are never initialized.
        PathPolicy(directory);
        using (var marker = OwnerFileProtection.CreateResult(Path.Combine(directory, Marker)))
            WriteResult(marker, new { schema = "platform-studio.foundation-owner-initialization.v1", request.CustomerId, request.CellId,
                operationId = Guid.NewGuid().ToString("N"), createdUtc = DateTimeOffset.UtcNow });
        var database = Path.Combine(directory, "cell.sqlite");
        var store = new FoundationStore(database, request.CellId, request.CustomerId);
        store.BootstrapAdministrator(request.AdministratorActorKey, request.ProjectId, request.EnvironmentId, request.ReviewerActorKey);
        return new { databasePath = database, identity = store.GetCellInstanceIdentity(), schema = store.SchemaStatus(),
            initializedNewDestination = true, activationPerformed = false, existingStateMigrated = false };
    }

    static object AuthorityCommand(Authority request)
    {
        Scope(request.CustomerId, request.CellId); var path = PathPolicy(request.AuthorityDirectory);
        if (request.Operation == "authority.provision") RejectAuthorityNamespace(path);
        var authority = request.Operation == "authority.provision"
            ? FoundationCellActivationAuthority.Provision(path, request.CustomerId, request.CellId)
            : new FoundationCellActivationAuthority(path, request.CustomerId, request.CellId);
        return new { binding = authority.ReadCurrent(), provisioned = request.Operation == "authority.provision", activationPerformed = false };
    }

    static object ActivateCell(Activate request)
    {
        Scope(request.CustomerId, request.CellId);
        var authority = new FoundationCellActivationAuthority(PathPolicy(request.AuthorityDirectory), request.CustomerId, request.CellId);
        return new { binding = authority.Activate(PathPolicy(request.DatabasePath, existingFile: true), request.DatabaseInstanceId,
            request.ExpectedGeneration, request.ExpectedBindingSha256), runtimesStoppedByCommand = false };
    }

    static object BackupCommand(Backup request)
    {
        Scope(request.CustomerId, request.CellId);
        if (request.Operation == "backup.verify" && request.DestinationDirectory is not null ||
            request.Operation == "backup.restore" && request.DestinationDirectory is null) throw Reject("OwnerBackupDestinationInvalid");
        var snapshot = PathPolicy(request.SnapshotPath, existingFile: true); var manifestPath = PathPolicy(request.ManifestPath, existingFile: true);
        if (request.PinnedPublicKeyPem.Length > 16384 || request.PinnedPublicKeyPem.Contains("PRIVATE", StringComparison.Ordinal) ||
            !PemEncoding.TryFind(request.PinnedPublicKeyPem, out var pem) || request.PinnedPublicKeyPem[pem.Label] != "PUBLIC KEY" ||
            pem.Location.GetOffsetAndLength(request.PinnedPublicKeyPem.Length) != (0, request.PinnedPublicKeyPem.Length)) throw Reject("OwnerPublicKeyRejected");
        using var key = RSA.Create(); key.ImportFromPem(request.PinnedPublicKeyPem);
        if (FoundationAuditAnchors.PublicKeyFingerprint(key) != request.PinnedPublicKeySha256) throw Reject("OwnerPublicKeyPinMismatch");
        var manifest = FoundationBackupSignatures.Parse(System.Text.Encoding.UTF8.GetString(ReadBounded(manifestPath, FoundationBackupSignatures.MaximumManifestBytes)));
        var claims = FoundationBackupSignatures.Verify(manifest, key, request.RestoreAuthority, request.CustomerId, request.CellId);
        var packet = new FoundationSignedBackup(new(snapshot, request.CustomerId, request.CellId, claims.SnapshotSha256,
            claims.SnapshotLength, claims.SnapshotCreatedUtc), manifest);
        // Windows denies writers/deletion while this source handle remains open through preflight and restore.
        using var source = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read);
        var verified = FoundationStore.VerifySignedBackup(packet, request.RestoreAuthority, key, request.CustomerId, request.CellId);
        if (request.Operation == "backup.verify") return new { commitment = verified, snapshotBytesVerified = true,
            sqliteAuditVerificationPerformed = true, activationPerformed = false, latestAuthorityUpdated = false };
        var directory = PathPolicy(request.DestinationDirectory!);
        if (File.Exists(directory) || Directory.Exists(directory)) throw Reject("OwnerDestinationAlreadyExists");
        if (!Directory.Exists(Path.GetDirectoryName(directory))) throw Reject("OwnerDestinationParentMissing");
        RejectAuthorityNamespace(directory);
        if (!OperatingSystem.IsWindows()) throw Reject("OwnerRestoreWindowsQualificationRequired");
        if (!OwnerFileProtection.CreateDirectory(directory)) throw Reject("OwnerDestinationReservationRejected");
        // Retain the fresh private directory as evidence if later restore I/O fails. Never replace existing state.
        PathPolicy(directory); var destination = Path.Combine(directory, "cell.sqlite");
        var restored = FoundationStore.RestoreSignedBackup(packet, destination, request.RestoreAuthority, key, request.CustomerId, request.CellId);
        return new { databasePath = destination, identity = restored.GetCellInstanceIdentity(), schema = restored.SchemaStatus(),
            activationPerformed = false, sourceHostFenced = false, latestAuthorityUpdated = false, recoveredWorkHeldForReconciliation = true };
    }

    static object IdentityProposal(Identity request)
    {
        Scope(request.CustomerId, request.CellId);
        if (request.IdentityContract.CustomerId != request.CustomerId || request.IdentityContract.CellId != request.CellId)
            throw Reject("OwnerIdentityBindingRejected");
        var proposal = EntraCellOnboarding.Create(request.IdentityContract);
        return new { publicCanonicalJson = proposal.CanonicalJson, proposal.Sha256, hostIdentity = proposal.HostIdentity,
            proposal.IdentityGrantAuthorized, proposal.ExecutionAuthorized, proposal.ProviderPermissionsVerified, proposal.RuntimeAcceptanceVerified };
    }

    static object InspectCell(string path, string customer, string cell)
    {
        Scope(customer, cell); var database = PathPolicy(path, existingFile: true);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        db.Open(); using var tx = db.BeginTransaction(deferred: true); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT schema_version,customer_id,cell_id FROM cell WHERE singleton=1";
        int version;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(1) != customer || reader.GetString(2) != cell) throw Reject("OwnerCellBindingRejected");
            version = reader.GetInt32(0); if (reader.Read()) throw Reject("OwnerCellBindingRejected");
        }
        command.CommandText = "PRAGMA quick_check";
        if (command.ExecuteScalar()?.ToString() != "ok") throw Reject("OwnerCellIntegrityRejected");
        tx.Commit();
        return new { databasePath = database, customerId = customer, cellId = cell, schemaVersion = version,
            supportedSchemaVersion = FoundationStore.CurrentSchemaVersion,
            state = version == FoundationStore.CurrentSchemaVersion ? "CurrentSchema" : version is >= 1 && version < FoundationStore.CurrentSchemaVersion ? "LegacySchemaRequiresStagedUpgrade" : "UnsupportedSchema",
            sqliteQuickCheckVerified = true, fullAuditVerificationPerformed = false, stateMutated = false };
    }

    static string PathPolicy(string value, bool existingFile = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value)) throw Reject("OwnerPathRejected");
        if (OperatingSystem.IsWindows() && value.Split('\\', '/').Skip(1).Any(p => p.EndsWith('.') || p.EndsWith(' ') || p.Contains(':') ||
            Regex.IsMatch(p.Split('.')[0], "\\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) throw Reject("OwnerPathRejected");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)); var root = Path.GetPathRoot(path);
        if (path.Length <= (root?.Length ?? 0)) throw Reject("OwnerPathRejected");
        if (OperatingSystem.IsWindows() && (root is null || !Regex.IsMatch(root, "\\A[A-Za-z]:\\\\\\z") || new DriveInfo(root).DriveType == DriveType.Network)) throw Reject("OwnerPathRejected");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            try { if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw Reject("OwnerLinkedPathRejected"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        if (existingFile && !File.Exists(path)) throw Reject("OwnerExistingFileRequired");
        return path;
    }
    static void ValidateResultNamespace(object request, string result)
    {
        if (!Path.GetFileName(result).EndsWith(".owner-result.json", StringComparison.Ordinal)) throw Reject("OwnerResultNameRejected");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var databases = new List<string>(); var authorities = new List<string>();
        switch (request)
        {
            case Inspect x: databases.Add(PathPolicy(x.DatabasePath)); break;
            case Initialize x: authorities.Add(PathPolicy(x.DestinationDirectory)); break;
            case Authority x: authorities.Add(PathPolicy(x.AuthorityDirectory)); break;
            case Activate x: authorities.Add(PathPolicy(x.AuthorityDirectory)); databases.Add(PathPolicy(x.DatabasePath)); break;
            case Backup x:
                databases.Add(PathPolicy(x.SnapshotPath)); databases.Add(PathPolicy(x.ManifestPath));
                if (x.DestinationDirectory is not null) authorities.Add(PathPolicy(x.DestinationDirectory)); break;
        }
        if (authorities.Any(root => string.Equals(result, root, comparison) || result.StartsWith(root + Path.DirectorySeparatorChar, comparison)) ||
            databases.Any(path => new[] { "", "-wal", "-shm", "-journal" }.Any(suffix => string.Equals(result, path + suffix, comparison))))
            throw Reject("OwnerProtectedResultNamespace");
        // Even an unrelated command cannot add files to a protected operational/software namespace.
        RejectAuthorityNamespace(result);
    }
    static void RejectAuthorityNamespace(string path)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (new[] { ".activation.guard", "authority.json", ".installation-owner.json", ".installation.guard", ".package-owner.json",
                    ".credential-namespace.json", ".vault.lock", ".keys.lock", "cell-binding.json", ".rollback.lock", ".backup-checkpoint.guard", ".foundation-owner-initialization.json",
                    ".credential-recovery-stage.guard", ".credential-recovery-stage.json", ".credential-recovery-checkpoint.guard", ".credential-recovery-checkpoint.json" }
                .Any(marker => Path.Exists(Path.Combine(parent, marker))) ||
                Directory.Exists(parent) && (Directory.EnumerateFileSystemEntries(parent, "*.credential.json").Any() || Directory.EnumerateFileSystemEntries(parent, "*.dpapi.json").Any()))
                throw Reject("OwnerProtectedResultNamespace");
    }
    static void Scope(string first, string second) { if (!CustomerContextResolver.ValidId(first) || !CustomerContextResolver.ValidId(second)) throw Reject("OwnerScopeRejected"); }
    static void Human(string actor)
    {
        var parts = actor?.Split(':') ?? [];
        if (parts.Length != 3 || parts[0] != "user" || parts.Skip(1).Any(p => !Guid.TryParseExact(p, "D", out var id) || id == Guid.Empty || id.ToString("D") != p))
            throw Reject("OwnerHumanIdentityRequired");
    }
    static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadBounded(stream, maximum);
    }
    static byte[] ReadBounded(FileStream stream, int maximum)
    {
        if (stream.Length is < 2 || stream.Length > maximum) throw Reject("OwnerInputSizeRejected");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    static T Parse<T>(JsonElement element) => element.Deserialize<T>(Json) ?? throw Reject("OwnerRequestInvalid");
    static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw Reject("OwnerRequestInvalid"); Unique(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
    static void WriteResult(Stream stream, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json); if (bytes.Length > 131072) throw Reject("OwnerResultSizeRejected");
        stream.Write(bytes); if (stream is FileStream file) file.Flush(flushToDisk: true);
    }
    static Rejection Reject(string code) => new(code);
}
