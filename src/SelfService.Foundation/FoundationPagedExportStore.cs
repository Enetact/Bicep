using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed record FoundationPagedExportSnapshot(string Id, string CustomerId, string CellId,
    string ProjectId, string EnvironmentId, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc,
    long AuditSequence, string AuditHash, long PolicyVersion, int PageCount, long ItemCount,
    string[] PageSha256, string Sha256)
{
    public bool MetadataOnly => true;
    public bool HistoricalSnapshot => true;
    public string CanonicalManifestBase64 { get; init; } = "";
}
public sealed record FoundationPagedExportItem(string Kind, JsonElement Metadata);
public sealed record FoundationPagedExportPage(string SnapshotId, int PageNumber,
    FoundationPagedExportItem[] Items, string PreviousPageSha256, string Sha256,
    string SnapshotSha256, string? NextCursor, bool Complete, DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc)
{
    public bool MetadataOnly => true;
    public bool HistoricalSnapshot => true;
    public string CanonicalPageBase64 { get; init; } = "";
}
public sealed record FoundationPagedExportStart(FoundationPagedExportSnapshot Snapshot,
    string FirstCursor, FoundationPagedExportPage FirstPage);
public sealed record FoundationPagedExportCleanup(int SnapshotsRemoved, int PagesRemoved,
    long LogicalBytesRemoved, bool SourceOrAuditDeleted = false, bool SecureErasureVerified = false);

public sealed partial class FoundationStore
{
    public const int MaximumPagedExportPageSize = 200;
    public const int MaximumPagedExportPages = 4096;
    public const int MaximumPagedExportPageBytes = 262144;
    public const int MaximumPagedExportPageResponseBytes = 1048576;
    public const int MaximumPagedExportStartResponseBytes = 2097152;
    public const long MaximumPagedExportSnapshotBytes = 128L * 1024 * 1024;
    public const long MaximumExportStorageBytes = 256L * 1024 * 1024;
    const string PagedExportPrefix = "xpg-";
    const string PagedRootSchema = "platform-studio.paged-scope-export.snapshot.v1";
    const string PagedPageSchema = "platform-studio.paged-scope-export.page.v1";
    static readonly JsonSerializerOptions PagedJson = new(JsonSerializerDefaults.Web)
    { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
      RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };

    sealed record PagedManifest(string Schema, string Id, string CustomerId, string CellId,
        string ProjectId, string EnvironmentId, string ActorKey, long ActorVersion, long PolicyVersion,
        long Created, long Expires, long AuditSequence, string AuditHash, int PageCount, long ItemCount, string[] PageSha256);
    sealed record PagedRoot(string Schema, PagedManifest Manifest, string SnapshotSha256,
        string FirstCursor, string[] PageRows);
    sealed record PagedPage(string Schema, string SnapshotId, string CustomerId, string CellId,
        string ProjectId, string EnvironmentId, string ActorKey, long ActorVersion, long PolicyVersion,
        int PageNumber, long Created, long Expires, string PreviousPageSha256,
        FoundationPagedExportItem[] Items, string? NextCursor, bool Complete);
    sealed record ExportRow(string Id, string Manifest, string Digest, long Created);

    /// <summary>Freeze metadata pages under one SQLite transaction, without loading all source rows into memory.</summary>
    public FoundationPagedExportStart CreatePagedScopeExport(string customerId, string auditorKey,
        string projectId, string environmentId, int pageSize = 100, TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        Scope(customerId); RequireHuman(auditorKey); ValidateApplicationScopeId(projectId); ValidateApplicationScopeId(environmentId);
        var duration = lifetime ?? TimeSpan.FromMinutes(15);
        if (pageSize is < 1 or > MaximumPagedExportPageSize || duration < TimeSpan.FromMinutes(1) || duration > TimeSpan.FromHours(1))
            throw new FoundationStoreException("InvalidExportPagePolicy", 400);
        cancellationToken.ThrowIfCancellationRequested();
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var actorVersion = Require(db, tx, auditorKey, projectId, environmentId, ["Auditor"], requireActive: false);
        ValidatePagedAuditChain(db, tx, cancellationToken);
        var policy = Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1");
        var tail = Rows(db, tx, "SELECT sequence,event_hash FROM audit WHERE customer_id=$c ORDER BY sequence DESC LIMIT 1",
            r => (Sequence: r.GetInt64(0), Hash: r.GetString(1)), ("$c", CustomerId)).SingleOrDefault();
        var id = PagedExportPrefix + NewId(); var created = Now; var expires = checked(created + (long)duration.TotalMilliseconds);
        var firstCursor = PagedCursor(); var currentCursor = firstCursor; var previousHash = "";
        var digests = new List<string>(); var pageRows = new List<string>(); var items = new List<FoundationPagedExportItem>();
        var storageBytes = Number(db, tx, "SELECT coalesce(sum(length(CAST(manifest AS BLOB))),0) FROM scope_exports WHERE customer_id=$c", ("$c", CustomerId));
        long capturedBytes = 0, itemCount = 0; var itemBytes = 0; PagedPage? firstPage = null;

        void Flush(bool complete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (digests.Count >= MaximumPagedExportPages) throw new FoundationStoreException("ExportPageCountExceeded", 413);
            var next = complete ? null : PagedCursor();
            var body = new PagedPage(PagedPageSchema, id, CustomerId, CellId, projectId, environmentId,
                auditorKey, actorVersion, policy, digests.Count, created, expires, previousHash, items.ToArray(), next, complete);
            var manifest = JsonSerializer.Serialize(body, PagedJson); var bytes = Encoding.UTF8.GetByteCount(manifest);
            if (bytes > MaximumPagedExportPageBytes) throw new FoundationStoreException("ExportItemTooLarge", 413);
            var rowId = PagedRowId(id, currentCursor); var digest = Hash(manifest);
            capturedBytes += bytes;
            if (capturedBytes > MaximumPagedExportSnapshotBytes || storageBytes + capturedBytes > MaximumExportStorageBytes)
                throw new FoundationStoreException("ExportStorageCapacityExceeded", 429);
            InsertPagedRow(db, tx, rowId, projectId, environmentId, manifest, digest, created);
            firstPage ??= body; digests.Add(digest); pageRows.Add(rowId); previousHash = digest;
            currentCursor = next ?? ""; items.Clear(); itemBytes = 0;
        }
        void Add(string kind, JsonElement metadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new FoundationPagedExportItem(kind, metadata);
            var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(item, PagedJson));
            if (bytes > MaximumPagedExportPageBytes - 16384) throw new FoundationStoreException("ExportItemTooLarge", 413);
            if (items.Count > 0 && (items.Count >= pageSize || itemBytes + bytes > MaximumPagedExportPageBytes - 16384)) Flush(false);
            items.Add(item); itemBytes += bytes; itemCount++;
        }
        string lastId = "";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = Rows(db, tx, """
                SELECT id,digest,length(content),created FROM artifacts
                WHERE customer_id=$c AND project_id=$p AND environment_id=$e AND id>$after ORDER BY id LIMIT 128
                """, r => new { id = r.GetString(0), sha256 = r.GetString(1), length = r.GetInt64(2), createdUtc = At(r.GetInt64(3)) },
                ("$c", CustomerId), ("$p", projectId), ("$e", environmentId), ("$after", lastId));
            foreach (var row in batch) { Add("artifact", JsonSerializer.SerializeToElement(row, PagedJson)); lastId = row.id; }
            if (batch.Count < 128) break;
        }
        lastId = "";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = Rows(db, tx, """
                SELECT id,digest,connection_generation,policy_version,expires FROM plans
                WHERE customer_id=$c AND project_id=$p AND environment_id=$e AND id>$after ORDER BY id LIMIT 128
                """, r => new { id = r.GetString(0), digest = r.GetString(1), connectionGeneration = r.GetInt64(2), policyVersion = r.GetInt64(3), expiresUtc = At(r.GetInt64(4)) },
                ("$c", CustomerId), ("$p", projectId), ("$e", environmentId), ("$after", lastId));
            foreach (var row in batch) { Add("plan", JsonSerializer.SerializeToElement(row, PagedJson)); lastId = row.id; }
            if (batch.Count < 128) break;
        }
        long lastSequence = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = Rows(db, tx, """
                SELECT sequence,customer_id,cell_id,event_id,actor_key,project_id,environment_id,action,target_id,outcome,reason,
                  correlation,created,previous_hash,event_hash FROM audit WHERE customer_id=$c AND project_id=$p AND environment_id=$e
                  AND sequence>$after AND sequence<=$tail ORDER BY sequence LIMIT 128
                """, AuditRow, ("$c", CustomerId), ("$p", projectId), ("$e", environmentId), ("$after", lastSequence), ("$tail", tail.Sequence));
            foreach (var row in batch) { Add("audit", JsonSerializer.SerializeToElement(row, PagedJson)); lastSequence = row.Sequence; }
            if (batch.Count < 128) break;
        }
        Flush(true);
        var publicManifest = new PagedManifest(PagedRootSchema, id, CustomerId, CellId, projectId, environmentId,
            auditorKey, actorVersion, policy, created, expires, tail.Sequence, tail.Hash ?? "", digests.Count, itemCount, digests.ToArray());
        var snapshotHash = Hash(JsonSerializer.Serialize(publicManifest, PagedJson));
        var root = new PagedRoot(PagedRootSchema, publicManifest, snapshotHash, firstCursor, pageRows.ToArray());
        var rootJson = JsonSerializer.Serialize(root, PagedJson); var rootBytes = Encoding.UTF8.GetByteCount(rootJson);
        if (rootBytes > 1048576 || capturedBytes + rootBytes > MaximumPagedExportSnapshotBytes ||
            storageBytes + capturedBytes + rootBytes > MaximumExportStorageBytes)
            throw new FoundationStoreException("ExportStorageCapacityExceeded", 429);
        cancellationToken.ThrowIfCancellationRequested();
        if (Now >= expires) throw new FoundationStoreException("ExportSnapshotExpired", 410);
        InsertPagedRow(db, tx, id, projectId, environmentId, rootJson, Hash(rootJson), created);
        Audit(db, tx, auditorKey, projectId, environmentId, "PagedScopeExported", id, "Succeeded", "FrozenMetadataSnapshot", id);
        cancellationToken.ThrowIfCancellationRequested();
        var result = BoundedPagedResponse(new FoundationPagedExportStart(PublicSnapshot(root), firstCursor,
            PublicPage(firstPage!, digests[0], snapshotHash)), MaximumPagedExportStartResponseBytes);
        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit(); return result;
    }

    public FoundationPagedExportStart GetPagedScopeExport(string customerId, string auditorKey, string projectId,
        string environmentId, string snapshotId, string expectedSnapshotSha256)
    {
        Scope(customerId); RequireHuman(auditorKey); ValidateApplicationScopeId(projectId); ValidateApplicationScopeId(environmentId);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var root = ReadPagedRoot(db, tx, auditorKey, projectId, environmentId, snapshotId, expectedSnapshotSha256);
        var page = ReadPagedPage(db, tx, root, root.FirstCursor);
        Audit(db, tx, auditorKey, projectId, environmentId, "PagedExportRead", snapshotId, "Succeeded", "FrozenMetadataRead", snapshotId);
        var result = BoundedPagedResponse(new FoundationPagedExportStart(PublicSnapshot(root), root.FirstCursor, page), MaximumPagedExportStartResponseBytes);
        tx.Commit(); return result;
    }

    public FoundationPagedExportPage ReadPagedScopeExportPage(string customerId, string auditorKey, string projectId,
        string environmentId, string snapshotId, string cursor, string expectedSnapshotSha256)
    {
        Scope(customerId); RequireHuman(auditorKey); ValidateApplicationScopeId(projectId); ValidateApplicationScopeId(environmentId);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var root = ReadPagedRoot(db, tx, auditorKey, projectId, environmentId, snapshotId, expectedSnapshotSha256);
        var page = ReadPagedPage(db, tx, root, cursor);
        Audit(db, tx, auditorKey, projectId, environmentId, "PagedExportRead", snapshotId, "Succeeded", "FrozenMetadataRead", snapshotId);
        var result = BoundedPagedResponse(page, MaximumPagedExportPageResponseBytes);
        tx.Commit(); return result;
    }

    public FoundationPagedExportCleanup PurgeExpiredPagedScopeExports(string customerId, string auditorKey,
        string projectId, string environmentId, int maximumSnapshots = 8)
    {
        Scope(customerId); RequireHuman(auditorKey); ValidateApplicationScopeId(projectId); ValidateApplicationScopeId(environmentId);
        if (maximumSnapshots is < 1 or > 16) throw new FoundationStoreException("InvalidPage", 400);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Require(db, tx, auditorKey, projectId, environmentId, ["Auditor"], requireActive: false);
        var roots = Rows(db, tx, """
            SELECT id,manifest,digest,created,length(CAST(manifest AS BLOB)) FROM scope_exports WHERE customer_id=$c AND project_id=$p AND environment_id=$e
            AND id LIKE 'xpg-%' AND id NOT LIKE 'xpg-page-%' AND created<=$before
            ORDER BY CASE WHEN json_valid(manifest) THEN json_extract(manifest,'$.manifest.expires') ELSE 0 END,id LIMIT $limit
            """, r => r.GetInt64(4) > 1048576 ? throw new FoundationStoreException("ExportIntegrityFailed")
                : new ExportRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)),
            ("$c", CustomerId), ("$p", projectId), ("$e", environmentId), ("$before", Now - 60000), ("$limit", maximumSnapshots));
        int snapshots = 0, pages = 0; long bytes = 0;
        foreach (var row in roots)
        {
            var root = ParsePagedRoot(row, projectId, environmentId);
            if (root.Manifest.Expires > Now) continue;
            for (var index = 0; index < root.PageRows.Length; index++)
            {
                var pageId = root.PageRows[index];
                var pageRow = FindPagedRow(db, tx, pageId, projectId, environmentId);
                // Validate every row's exact expected commitment before removing any part of an expired snapshot.
                MatchDigest(pageRow.Digest, root.Manifest.PageSha256[index]);
                MatchDigest(Hash(pageRow.Manifest), pageRow.Digest);
                bytes += Encoding.UTF8.GetByteCount(pageRow.Manifest);
                Exec(db, tx, "DELETE FROM scope_exports WHERE customer_id=$c AND id=$id AND project_id=$p AND environment_id=$e",
                    ("$c", CustomerId), ("$id", pageId), ("$p", projectId), ("$e", environmentId)); pages++;
            }
            bytes += Encoding.UTF8.GetByteCount(row.Manifest);
            Exec(db, tx, "DELETE FROM scope_exports WHERE customer_id=$c AND id=$id", ("$c", CustomerId), ("$id", row.Id)); snapshots++;
            Audit(db, tx, auditorKey, projectId, environmentId, "ExpiredPagedExportPurged", row.Id, "Succeeded", "ProtocolCacheExpired", row.Id);
        }
        tx.Commit(); return new(snapshots, pages, bytes);
    }

    static string PagedCursor() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    static string PagedRowId(string id, string cursor) => "xpg-page-" + Hash(id + ":" + cursor);
    static bool PagedDigest(string value) => value is not null && Regex.IsMatch(value, "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    void InsertPagedRow(SqliteConnection db, SqliteTransaction tx, string id, string project, string environment,
        string manifest, string digest, long created) => Exec(db, tx, "INSERT INTO scope_exports VALUES($c,$id,$p,$e,$m,$d,$now)",
            ("$c", CustomerId), ("$id", id),
            ("$p", project), ("$e", environment), ("$m", manifest), ("$d", digest), ("$now", created));

    ExportRow FindPagedRow(SqliteConnection db, SqliteTransaction tx, string id, string project, string environment) =>
        Rows(db, tx, "SELECT id,manifest,digest,created,length(CAST(manifest AS BLOB)) FROM scope_exports WHERE customer_id=$c AND id=$id AND project_id=$p AND environment_id=$e",
            r => r.GetInt64(4) > (id.StartsWith("xpg-page-", StringComparison.Ordinal) ? MaximumPagedExportPageBytes : 1048576)
                ? throw new FoundationStoreException("ExportIntegrityFailed")
                : new ExportRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)), ("$c", CustomerId), ("$id", id),
            ("$p", project), ("$e", environment)).SingleOrDefault() ?? throw new FoundationStoreException("NotFound", 404);

    PagedRoot ParsePagedRoot(ExportRow row, string project, string environment)
    {
        if (Encoding.UTF8.GetByteCount(row.Manifest) > 1048576) throw new FoundationStoreException("ExportIntegrityFailed");
        MatchDigest(Hash(row.Manifest), row.Digest);
        var root = ReadPagedJson<PagedRoot>(row.Manifest); var m = root.Manifest;
        if (root.Schema != PagedRootSchema || m.Schema != PagedRootSchema || m.Id != row.Id || m.CustomerId != CustomerId || m.CellId != CellId ||
            m.ProjectId != project || m.EnvironmentId != environment || m.Created != row.Created || m.Created < 0 || m.Expires > 253402300799999 ||
            m.Expires <= m.Created || m.Expires - m.Created is < 60000 or > 3600000 ||
            m.PageCount is < 1 or > MaximumPagedExportPages || m.PageSha256.Length != m.PageCount || root.PageRows.Length != m.PageCount ||
            root.PageRows.Distinct(StringComparer.Ordinal).Count() != m.PageCount || root.PageRows.Any(p => p is null || !Regex.IsMatch(p, "\\Axpg-page-[a-f0-9]{64}\\z")) ||
            m.PageSha256.Any(p => !PagedDigest(p)) || !PagedDigest(root.FirstCursor) || !PagedDigest(root.SnapshotSha256) ||
            m.ItemCount < 0 || m.ItemCount > (long)m.PageCount * MaximumPagedExportPageSize || m.PolicyVersion <= 0 || m.ActorVersion <= 0 ||
            m.AuditSequence < 0 || m.AuditSequence > MaximumAuditEventsPerCell ||
            m.AuditSequence > 0 && !PagedDigest(m.AuditHash) || root.PageRows[0] != PagedRowId(m.Id, root.FirstCursor))
            throw new FoundationStoreException("ExportIntegrityFailed");
        MatchDigest(Hash(JsonSerializer.Serialize(m, PagedJson)), root.SnapshotSha256); return root;
    }

    PagedRoot ReadPagedRoot(SqliteConnection db, SqliteTransaction tx, string actor, string project, string environment,
        string id, string expectedDigest)
    {
        ValidateId(id);
        if (!Regex.IsMatch(id, "\\Axpg-[a-f0-9]{32}\\z") || !PagedDigest(expectedDigest)) throw new FoundationStoreException("InvalidExportCursor", 400);
        var version = Require(db, tx, actor, project, environment, ["Auditor"], requireActive: false);
        var root = ParsePagedRoot(FindPagedRow(db, tx, id, project, environment), project, environment);
        MatchDigest(root.SnapshotSha256, expectedDigest);
        if (root.Manifest.ActorKey != actor) throw new FoundationStoreException("NotFound", 404);
        if (root.Manifest.ActorVersion != version) throw new FoundationStoreException("ExportAuthorityChanged", 403);
        if (root.Manifest.PolicyVersion != Number(db, tx, "SELECT policy_version FROM cell WHERE singleton=1")) throw new FoundationStoreException("ExportPolicyChanged", 409);
        if (root.Manifest.Created > Now) throw new FoundationStoreException("ExportSnapshotNotYetValid", 409);
        if (root.Manifest.Expires <= Now) throw new FoundationStoreException("ExportSnapshotExpired", 410);
        return root;
    }
    FoundationPagedExportPage ReadPagedPage(SqliteConnection db, SqliteTransaction tx, PagedRoot root, string cursor)
    {
        if (!PagedDigest(cursor)) throw new FoundationStoreException("InvalidExportCursor", 400);
        var row = FindPagedRow(db, tx, PagedRowId(root.Manifest.Id, cursor), root.Manifest.ProjectId, root.Manifest.EnvironmentId);
        MatchDigest(Hash(row.Manifest), row.Digest);
        if (Encoding.UTF8.GetByteCount(row.Manifest) > MaximumPagedExportPageBytes) throw new FoundationStoreException("ExportIntegrityFailed");
        var page = ReadPagedJson<PagedPage>(row.Manifest); var m = root.Manifest; var index = page.PageNumber;
        if (index < 0 || index >= m.PageCount || root.PageRows[index] != row.Id || page.Schema != PagedPageSchema || page.SnapshotId != m.Id ||
            page.CustomerId != CustomerId || page.CellId != CellId || page.ProjectId != m.ProjectId || page.EnvironmentId != m.EnvironmentId ||
            page.ActorKey != m.ActorKey || page.ActorVersion != m.ActorVersion || page.PolicyVersion != m.PolicyVersion ||
            page.Created != m.Created || page.Expires != m.Expires || page.Items.Length > MaximumPagedExportPageSize ||
            page.Items.Length == 0 && (m.PageCount != 1 || m.ItemCount != 0) ||
            page.Items.Any(i => i is null || i.Kind is not ("artifact" or "plan" or "audit") || i.Metadata.ValueKind != JsonValueKind.Object) ||
            page.PreviousPageSha256 != (index == 0 ? "" : m.PageSha256[index - 1]) ||
            page.Complete != (index == m.PageCount - 1) || page.Complete && page.NextCursor is not null ||
            !page.Complete && (!PagedDigest(page.NextCursor!) || PagedRowId(m.Id, page.NextCursor!) != root.PageRows[index + 1]))
            throw new FoundationStoreException("ExportIntegrityFailed");
        MatchDigest(row.Digest, m.PageSha256[index]);
        return PublicPage(page, row.Digest, root.SnapshotSha256);
    }
    static FoundationPagedExportSnapshot PublicSnapshot(PagedRoot root)
    {
        var m = root.Manifest;
        return new(m.Id, m.CustomerId, m.CellId, m.ProjectId, m.EnvironmentId, At(m.Created), At(m.Expires),
            m.AuditSequence, m.AuditHash, m.PolicyVersion, m.PageCount, m.ItemCount, m.PageSha256.ToArray(), root.SnapshotSha256)
            { CanonicalManifestBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, PagedJson))) };
    }
    static FoundationPagedExportPage PublicPage(PagedPage page, string digest, string snapshotDigest) =>
        new(page.SnapshotId, page.PageNumber, page.Items, page.PreviousPageSha256, digest, snapshotDigest,
            page.NextCursor, page.Complete, At(page.Created), At(page.Expires))
            { CanonicalPageBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(page, PagedJson))) };

    static T BoundedPagedResponse<T>(T response, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(response, PagedJson)) > maximumBytes)
            throw new FoundationStoreException("ExportResponseTooLarge", 413);
        return response;
    }

    void ValidatePagedAuditChain(SqliteConnection db, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        // Verify the entire cell chain in bounded SQL/keyset batches while the capture transaction holds its snapshot.
        var previous = ""; long sequence = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = Rows(db, tx, """
                SELECT sequence,customer_id,cell_id,event_id,actor_key,project_id,environment_id,action,target_id,outcome,reason,
                  correlation,created,previous_hash,event_hash FROM audit WHERE sequence>$after ORDER BY sequence LIMIT 128
                """, AuditRow, ("$after", sequence));
            foreach (var row in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sequence >= MaximumAuditEventsPerCell) throw new FoundationStoreException("AuditVerificationLimit");
                if (row.Sequence != ++sequence || row.CustomerId != CustomerId || row.CellId != CellId || row.PreviousHash != previous ||
                    row.EventHash != AuditHash(row.Sequence, row.CustomerId, row.CellId, row.EventId, row.ActorKey, row.ProjectId,
                        row.EnvironmentId, row.Action, row.TargetId, row.Outcome, row.Reason, row.CorrelationId,
                        row.CreatedAt.ToUnixTimeMilliseconds(), row.PreviousHash)) throw new FoundationStoreException("AuditIntegrityFailed");
                previous = row.EventHash;
            }
            if (batch.Count < 128) break;
        }
    }

    static T ReadPagedJson<T>(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 16 });
            Unique(document.RootElement);
            return document.RootElement.Deserialize<T>(PagedJson) ?? throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or ArgumentException) { throw new FoundationStoreException("ExportIntegrityFailed"); }
        static void Unique(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in e.EnumerateObject()) { if (!names.Add(p.Name)) throw new JsonException(); Unique(p.Value); }
            }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var child in e.EnumerateArray()) Unique(child);
        }
    }
}
