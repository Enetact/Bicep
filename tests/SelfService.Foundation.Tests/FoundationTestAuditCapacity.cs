using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;

namespace SelfService.Foundation.Tests;

// Synthetic standalone snapshots only. Preserve the real append-only guards and generate the production hash chain.
internal static class FoundationTestAuditCapacity
{
    delegate string AuditHasher(long sequence, string customer, string cell, string eventId, string actor, string project,
        string environment, string action, string target, string outcome, string reason, string correlation, long created, string previous);
    static readonly AuditHasher Hash = typeof(FoundationStore).GetMethod("AuditHash", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<AuditHasher>();

    internal static FoundationBackup FillSnapshot(FoundationBackup snapshot, TimeProvider clock, int availableAuditEvents = 0)
    {
        if (availableAuditEvents is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(availableAuditEvents));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot.DatabasePath, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString()))
        {
            db.Open(); using var tx = db.BeginTransaction(); long sequence = 0; var previous = "";
            using (var tail = db.CreateCommand())
            {
                tail.Transaction = tx; tail.CommandText = "SELECT sequence,event_hash FROM audit ORDER BY sequence DESC LIMIT 1";
                using var row = tail.ExecuteReader(); if (row.Read()) { sequence = row.GetInt64(0); previous = row.GetString(1); }
            }
            using var insert = db.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = "INSERT INTO audit VALUES($sequence,$customer,$cell,$event,$actor,$project,$environment,$action,$target,$outcome,$reason,$correlation,$created,$previous,$hash)";
            foreach (var name in new[] { "$sequence", "$created" }) insert.Parameters.Add(name, SqliteType.Integer);
            foreach (var name in new[] { "$customer", "$cell", "$event", "$actor", "$project", "$environment", "$action", "$target", "$outcome", "$reason", "$correlation", "$previous", "$hash" }) insert.Parameters.Add(name, SqliteType.Text);
            const string actor = "synthetic-capacity", project = "default", environment = "dev", action = "SyntheticCapacity", outcome = "Succeeded", reason = "LocalCapacityFixture", correlation = "synthetic-capacity";
            var created = clock.GetUtcNow().ToUnixTimeMilliseconds();
            insert.Parameters["$customer"].Value = snapshot.CustomerId; insert.Parameters["$cell"].Value = snapshot.CellId; insert.Parameters["$actor"].Value = actor;
            insert.Parameters["$project"].Value = project; insert.Parameters["$environment"].Value = environment; insert.Parameters["$action"].Value = action;
            insert.Parameters["$target"].Value = snapshot.CellId; insert.Parameters["$outcome"].Value = outcome; insert.Parameters["$reason"].Value = reason;
            insert.Parameters["$correlation"].Value = correlation; insert.Parameters["$created"].Value = created; insert.Prepare();
            for (sequence++; sequence <= FoundationStore.MaximumAuditEventsPerCell - availableAuditEvents; sequence++)
            {
                var eventId = Guid.NewGuid().ToString("N"); var hash = Hash(sequence, snapshot.CustomerId, snapshot.CellId, eventId, actor, project,
                    environment, action, snapshot.CellId, outcome, reason, correlation, created, previous);
                insert.Parameters["$sequence"].Value = sequence; insert.Parameters["$event"].Value = eventId; insert.Parameters["$previous"].Value = previous; insert.Parameters["$hash"].Value = hash;
                if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Synthetic audit capacity insertion failed."); previous = hash;
            }
            tx.Commit();
        }
        var bytes = File.ReadAllBytes(snapshot.DatabasePath);
        return snapshot with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Length = bytes.Length };
    }

    internal static FoundationSignedBackup Sign(FoundationBackup snapshot, RSA key, TimeProvider clock)
    {
        var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
        var manifest = new FoundationBackupSignature(1, "cell-snapshot", Guid.NewGuid().ToString("N"), snapshot.CustomerId, snapshot.CellId,
            FoundationStore.CurrentSchemaVersion, 1, snapshot.Sha256, snapshot.Length, DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CreatedAt.ToUnixTimeMilliseconds()),
            now, now.AddMinutes(-1), now.AddHours(1), "synthetic-capacity-key", FoundationAuditAnchors.PublicKeyFingerprint(key), FoundationBackupSignatures.Algorithm, "");
        return new(snapshot, manifest with { Signature = Convert.ToBase64String(key.SignData(FoundationBackupSignatures.CanonicalPayload(manifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) });
    }
}
