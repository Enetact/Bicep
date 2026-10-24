using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    const string ConfigurationKind = "config.update";
    const int ConfigurationHistoryLimit = 2048;
    static readonly JsonSerializerOptions ConfigurationJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16
    };
    sealed record ConfigurationChangePayload(int Schema, long Generation, long ExpectedGeneration, string PreviousDigest,
        FoundationFeatureConfiguration Configuration, string Purpose, long? RollbackOfGeneration = null);

    /// <summary>Safe metadata remains readable after technical entitlement expiry or customer suspension.</summary>
    public FoundationConfigurationSnapshot ReadConfiguration(string customerId, string actorKey, string projectId, string environmentId)
    {
        Scope(customerId); RequireHuman(actorKey); ValidateApplicationScopeId(projectId); ValidateApplicationScopeId(environmentId);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        Require(db, tx, actorKey, projectId, environmentId, ["CustomerAdmin", "Approver", "Auditor"], requireActive: false);
        var result = CurrentConfiguration(ReadConfigurationHistory(db, tx), projectId, environmentId);
        tx.Commit(); return result;
    }

    public FoundationAdministrativePlan PrepareConfigurationChange(string customerId, string requesterKey, string projectId,
        string environmentId, FoundationFeatureConfiguration configuration, long expectedGeneration, string purpose,
        EntitlementVerifier verifier, string runtimeVersion) => PrepareConfigurationCore(customerId, requesterKey, projectId,
            environmentId, configuration, expectedGeneration, purpose, verifier, runtimeVersion, null);

    /// <summary>Rollback is a new reviewed generation; it cannot restore an old authorization fence or entitlement sequence.</summary>
    public FoundationAdministrativePlan PrepareConfigurationRollback(string customerId, string requesterKey, string projectId,
        string environmentId, long targetGeneration, long expectedGeneration, string purpose, EntitlementVerifier verifier, string runtimeVersion)
    {
        Scope(customerId); RequireHuman(requesterKey); ValidateApplicationScopeId(projectId); ValidateApplicationScopeId(environmentId);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        Require(db, tx, requesterKey, projectId, environmentId, ["CustomerAdmin"]);
        var history = ReadConfigurationHistory(db, tx);
        var current = CurrentConfiguration(history, projectId, environmentId);
        if (targetGeneration < 0 || targetGeneration >= current.Generation) throw new FoundationStoreException("InvalidRollbackGeneration", 400);
        var target = targetGeneration == 0 ? DefaultConfiguration(projectId, environmentId) :
            history.SingleOrDefault(item => item.ProjectId == projectId && item.EnvironmentId == environmentId && item.Generation == targetGeneration)
                ?? throw new FoundationStoreException("ConfigurationGenerationNotFound", 404);
        tx.Commit();
        return PrepareConfigurationCore(customerId, requesterKey, projectId, environmentId, target.Configuration,
            expectedGeneration, purpose, verifier, runtimeVersion, targetGeneration);
    }

    FoundationAdministrativePlan PrepareConfigurationCore(string customerId, string requesterKey, string projectId,
        string environmentId, FoundationFeatureConfiguration configuration, long expectedGeneration, string purpose,
        EntitlementVerifier verifier, string runtimeVersion, long? rollbackOfGeneration)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        Scope(customerId); RequireHuman(requesterKey); ValidateApplicationScopeId(projectId); ValidateApplicationScopeId(environmentId); ValidatePurpose(purpose);
        if (expectedGeneration < 0) throw new FoundationStoreException("InvalidConfigurationGeneration", 400);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Require(db, tx, requesterKey, projectId, environmentId, ["CustomerAdmin"]);
        var history = ReadConfigurationHistory(db, tx);
        if (history.Count >= ConfigurationHistoryLimit) throw new FoundationStoreException("ConfigurationHistoryCapacityExceeded", 429);
        var current = CurrentConfiguration(history, projectId, environmentId);
        if (current.Generation != expectedGeneration) throw new FoundationStoreException("ConfigurationGenerationChanged");
        var normalized = NormalizeFeatureConfiguration(configuration);
        ValidateFeatureConfiguration(normalized, verifier, runtimeVersion, history);
        var payload = new ConfigurationChangePayload(1, checked(current.Generation + 1), current.Generation, current.Digest, normalized, purpose, rollbackOfGeneration);
        var plan = CreateAdministrativePlan(db, tx, requesterKey, projectId, environmentId, ConfigurationKind, JsonSerializer.Serialize(payload, ConfigurationJson));
        tx.Commit(); return plan;
    }

    public FoundationConfigurationSnapshot ExecuteConfigurationChange(string customerId, string executorKey, string id,
        string expectedDigest, EntitlementVerifier verifier, string runtimeVersion)
    {
        ArgumentNullException.ThrowIfNull(verifier); Scope(customerId); RequireHuman(executorKey); ValidateId(id);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var plan = RequireAdministrativeExecution(db, tx, executorKey, id, expectedDigest, ConfigurationKind);
        var payload = ReadConfigurationPayload(plan.PayloadJson);
        var history = ReadConfigurationHistory(db, tx);
        var current = CurrentConfiguration(history, plan.ProjectId, plan.EnvironmentId);
        if (payload.ExpectedGeneration != current.Generation || payload.Generation != current.Generation + 1 || payload.PreviousDigest != current.Digest)
            throw new FoundationStoreException("ConfigurationGenerationChanged");
        if (history.Count >= ConfigurationHistoryLimit) throw new FoundationStoreException("ConfigurationHistoryCapacityExceeded", 429);
        ValidateFeatureConfiguration(payload.Configuration, verifier, runtimeVersion, history);
        CompleteAdministrativePlan(db, tx, executorKey, plan, "ConfigurationChanged");
        // Every reviewed feature/config change fences all plans and queued jobs in this customer cell.
        Exec(db, tx, "UPDATE cell SET policy_version=policy_version+1 WHERE singleton=1");
        var result = ConfigurationSnapshot(plan, payload); tx.Commit(); return result;
    }

    public FoundationFeatureDecision EvaluateFeature(string customerId, string actorKey, string projectId, string environmentId,
        string feature, EntitlementVerifier verifier, string runtimeVersion)
    {
        ArgumentNullException.ThrowIfNull(verifier); Scope(customerId); ValidateId(actorKey);
        ValidateApplicationScopeId(projectId); ValidateApplicationScopeId(environmentId);
        if (!FoundationFeatures.IsKnown(feature)) throw new FoundationStoreException("UnknownFeature", 400);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        var result = EvaluateFeatureCore(db, tx, actorKey, projectId, environmentId, feature, verifier, runtimeVersion);
        tx.Commit(); return result;
    }

    FoundationFeatureDecision EvaluateFeatureCore(SqliteConnection db, SqliteTransaction tx, string actorKey, string projectId,
        string environmentId, string feature, EntitlementVerifier verifier, string runtimeVersion)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (!FoundationFeatures.IsKnown(feature)) throw new FoundationStoreException("UnknownFeature", 400);
        var safe = FoundationFeatures.AlwaysAvailable.Contains(feature);
        // Feature availability is an additional restriction, never a substitute for role/capability authorization.
        var roles = feature switch
        {
            FoundationFeatures.Administration => new[] { "CustomerAdmin" },
            FoundationFeatures.AuditRead or FoundationFeatures.ExportRead => ["Auditor"],
            _ => ["Viewer", "Requester", "Approver", "Operator", "CustomerAdmin", "Auditor", "PlatformOperator"]
        };
        Require(db, tx, actorKey, projectId, environmentId, roles, requireActive: !safe);
        if (safe)
        {
            // No license/config parsing dependency may strand recovery, evidence access, or authorized exports.
            return new(feature, true, "AlwaysAvailable", 0, "", "ServerPolicy");
        }
        var history = ReadConfigurationHistory(db, tx);
        var snapshot = CurrentConfiguration(history, projectId, environmentId);
        var configuration = snapshot.Configuration;
        FoundationFeatureDecision Decision(bool enabled, string reason) => new(feature, enabled, reason, snapshot.Generation, snapshot.Digest, configuration.Mode);
        FoundationFeatureDecision result;
        try
        {
            ValidateFeatureConfiguration(configuration, verifier, runtimeVersion, history);
            result = !configuration.EnabledFeatures.Contains(feature, StringComparer.Ordinal) ? Decision(false, "FeatureDisabled") :
                !InConfigurationRollout(configuration.RolloutPercent, projectId, environmentId, feature) ? Decision(false, "OutsideRollout") :
                Decision(true, configuration.Mode == "LocalPreview" ? "LocalPreview" : "SignedEntitlement");
        }
        catch (FoundationStoreException ex) when (ex.Code.StartsWith("Entitlement", StringComparison.Ordinal) ||
            ex.Code is "RuntimeIncompatible" or "InvalidRuntimeVersion")
        { result = Decision(false, ex.Code); }
        return result;
    }

    // StartMockDispatch calls this inside the same write transaction that admits the durable dispatch intent.
    void RequireMockDispatchFeature(SqliteConnection db, SqliteTransaction tx, string requesterKey, string projectId,
        string environmentId, EntitlementVerifier verifier, string runtimeVersion)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        try
        {
            if (EvaluateFeatureCore(db, tx, requesterKey, projectId, environmentId,
                FoundationFeatures.PipelineMockDispatch, verifier, runtimeVersion).Enabled) return;
        }
        catch (FoundationStoreException) { /* A denied or unreadable feature policy cannot admit a dispatch intent. */ }
        throw new FoundationStoreException("DispatchFeatureUnavailable", 403);
    }

    public FoundationFeatureDecision RequireFeature(string customerId, string actorKey, string projectId, string environmentId,
        string feature, EntitlementVerifier verifier, string runtimeVersion)
    {
        var result = EvaluateFeature(customerId, actorKey, projectId, environmentId, feature, verifier, runtimeVersion);
        if (!result.Enabled) throw new FoundationStoreException(result.Reason, 403);
        return result;
    }

    List<FoundationConfigurationSnapshot> ReadConfigurationHistory(SqliteConnection db, SqliteTransaction? tx)
    {
        var records = Rows(db, tx, """
            SELECT p.id,p.project_id,p.environment_id,p.requester,p.requester_version,p.policy_version,p.payload,p.digest,p.expires,p.approver,p.approver_version,
              (SELECT count(*) FROM administrative_approvals a WHERE a.customer_id=p.customer_id AND a.plan_id=p.id
                AND a.actor_key=p.approver AND a.actor_version=p.approver_version AND a.digest=p.digest), coalesce(r.receipts,0)
            FROM administrative_plans p LEFT JOIN
              (SELECT target_id,count(*) receipts FROM audit WHERE customer_id=$c AND action='ConfigurationChanged' AND outcome='Succeeded' GROUP BY target_id) r
              ON r.target_id=p.id
            WHERE p.customer_id=$c AND p.kind='config.update' AND p.state='Executed' ORDER BY p.created,p.id LIMIT 2049
            """, r => (Plan: new FoundationAdministrativePlan(r.GetString(0), CustomerId, CellId, ConfigurationKind, r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetString(6), r.GetString(7), At(r.GetInt64(8)), "Executed",
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetInt64(10)), Approvals: r.GetInt64(11), Receipts: r.GetInt64(12)), ("$c", CustomerId));
        if (records.Count > ConfigurationHistoryLimit) throw new FoundationStoreException("ConfigurationHistoryIntegrityFailed");
        var history = new List<FoundationConfigurationSnapshot>();
        var payloads = new Dictionary<string, ConfigurationChangePayload>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var plan = record.Plan;
            MatchDigest(AdministrativeDigest(plan), plan.Digest);
            if (plan.ApproverKey is null || plan.ApproverKey == plan.RequesterKey || record.Approvals != 1 || record.Receipts != 1)
                throw new FoundationStoreException("ConfigurationHistoryIntegrityFailed");
            var payload = ReadConfigurationPayload(plan.PayloadJson);
            payloads.Add(plan.Id, payload);
            history.Add(ConfigurationSnapshot(plan, payload));
        }
        foreach (var group in history.GroupBy(item => (item.ProjectId, item.EnvironmentId)))
        {
            var previous = DefaultConfiguration(group.Key.ProjectId, group.Key.EnvironmentId);
            foreach (var current in group.OrderBy(item => item.Generation))
            {
                var payload = payloads[current.AdministrativePlanId!];
                if (payload.Generation != previous.Generation + 1 || payload.ExpectedGeneration != previous.Generation || payload.PreviousDigest != previous.Digest)
                    throw new FoundationStoreException("ConfigurationHistoryIntegrityFailed");
                previous = current;
            }
        }
        return history;
    }

    FoundationConfigurationSnapshot CurrentConfiguration(List<FoundationConfigurationSnapshot> history, string projectId, string environmentId) =>
        history.Where(item => item.ProjectId == projectId && item.EnvironmentId == environmentId).MaxBy(item => item.Generation) ?? DefaultConfiguration(projectId, environmentId);

    FoundationConfigurationSnapshot DefaultConfiguration(string projectId, string environmentId)
    {
        var configuration = new FoundationFeatureConfiguration("LocalPreview", FoundationFeatures.Available.Order(StringComparer.Ordinal).ToArray(), 100, "0.0.0", "65535.65535.65535");
        return new(CustomerId, CellId, projectId, environmentId, 0, Hash(JsonSerializer.Serialize(new
            { schema = 1, CustomerId, CellId, projectId, environmentId, generation = 0, configuration }, Json)), null, configuration);
    }

    FoundationConfigurationSnapshot ConfigurationSnapshot(FoundationAdministrativePlan plan, ConfigurationChangePayload payload) =>
        new(CustomerId, CellId, plan.ProjectId, plan.EnvironmentId, payload.Generation, plan.Digest, plan.Id, payload.Configuration);

    static ConfigurationChangePayload ReadConfigurationPayload(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<ConfigurationChangePayload>(CanonicalJson(json), ConfigurationJson);
            if (payload is null || payload.Schema != 1 || payload.Generation < 1 || payload.ExpectedGeneration < 0 ||
                payload.ExpectedGeneration != payload.Generation - 1 || payload.PreviousDigest is null || payload.PreviousDigest.Length != 64 ||
                payload.RollbackOfGeneration is < 0 || payload.RollbackOfGeneration >= payload.ExpectedGeneration)
                throw new FoundationStoreException("ConfigurationHistoryIntegrityFailed");
            ValidatePurpose(payload.Purpose);
            return payload with { Configuration = NormalizeFeatureConfiguration(payload.Configuration) };
        }
        catch (JsonException) { throw new FoundationStoreException("ConfigurationHistoryIntegrityFailed"); }
    }

    static FoundationFeatureConfiguration NormalizeFeatureConfiguration(FoundationFeatureConfiguration value)
    {
        if (value is null || value.Mode is not ("LocalPreview" or "Licensed") || value.EnabledFeatures is null || value.EnabledFeatures.Length > 32 ||
            value.RolloutPercent is < 0 or > 100 ||
            (value.Mode == "LocalPreview") != (value.Entitlement is null))
            throw new FoundationStoreException("InvalidFeatureConfiguration", 400);
        var features = value.EnabledFeatures.ToArray();
        if (features.Any(feature => !FoundationFeatures.Available.Contains(feature)) ||
            features.Distinct(StringComparer.OrdinalIgnoreCase).Count() != features.Length)
            throw new FoundationStoreException("InvalidFeatureConfiguration", 400);
        var minimum = ParseRuntimeVersion(value.MinimumRuntimeVersion); var maximum = ParseRuntimeVersion(value.MaximumRuntimeVersion);
        if (minimum > maximum) throw new FoundationStoreException("InvalidRuntimeWindow", 400);
        var entitlement = value.Entitlement is null ? null : value.Entitlement with { Document = EntitlementVerifier.Snapshot(value.Entitlement.Document) };
        return value with { EnabledFeatures = features.Order(StringComparer.Ordinal).ToArray(), Entitlement = entitlement };
    }

    void ValidateFeatureConfiguration(FoundationFeatureConfiguration configuration, EntitlementVerifier verifier, string runtimeVersion,
        List<FoundationConfigurationSnapshot> history)
    {
        if (verifier.RequireSignedMode && configuration.Mode != "Licensed")
            throw new FoundationStoreException("EntitlementSignedModeRequired", 403);
        var runtime = ParseRuntimeVersion(runtimeVersion);
        if (runtime < ParseRuntimeVersion(configuration.MinimumRuntimeVersion) || runtime > ParseRuntimeVersion(configuration.MaximumRuntimeVersion))
            throw new FoundationStoreException("RuntimeIncompatible", 409);
        if (configuration.Mode == "LocalPreview") return;
        var entitlement = verifier.Verify(configuration.Entitlement!, CustomerId, CellId);
        if (configuration.EnabledFeatures.Any(feature => !entitlement.Features.Contains(feature)))
            throw new FoundationStoreException("EntitlementFeatureDenied", 403);
        // Customer/cell-wide high water mark, including retired configurations and other project/environment scopes.
        var prior = history.Where(item => item.Configuration.Entitlement is not null)
            .Select(item => item.Configuration.Entitlement!)
            .Where(item => item.Document.Issuer == entitlement.Issuer && item.Document.LicenseId == entitlement.LicenseId).ToArray();
        if (prior.Length == 0) return;
        var highest = prior.Max(item => item.Document.Sequence);
        if (entitlement.Sequence < highest) throw new FoundationStoreException("EntitlementReplayDenied", 403);
        if (prior.Where(item => item.Document.Sequence == entitlement.Sequence).Any(item =>
            Convert.ToHexStringLower(SHA256.HashData(EntitlementVerifier.CanonicalDocument(item.Document))) != entitlement.Digest))
            throw new FoundationStoreException("EntitlementSequenceConflict", 403);
    }

    bool InConfigurationRollout(int percent, string projectId, string environmentId, string feature)
    {
        // A scope-wide cohort is stable across requester, reviewer, and worker identities.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { CustomerId, CellId, projectId, environmentId, feature }, Json)));
        return BinaryPrimitives.ReadUInt32BigEndian(bytes) % 100 < percent;
    }

    static Version ParseRuntimeVersion(string? value)
    {
        if (value is null || value.Length > 20 || !System.Text.RegularExpressions.Regex.IsMatch(value, "\\A(0|[1-9][0-9]{0,4})\\.(0|[1-9][0-9]{0,4})\\.(0|[1-9][0-9]{0,4})\\z") ||
            !Version.TryParse(value, out var parsed) || parsed.Major > 65535 || parsed.Minor > 65535 || parsed.Build > 65535)
            throw new FoundationStoreException("InvalidRuntimeVersion", 400);
        return parsed;
    }
}
