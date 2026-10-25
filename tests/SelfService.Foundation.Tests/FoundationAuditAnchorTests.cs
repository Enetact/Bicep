using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationAuditAnchorTests
{
    [Fact]
    public void Independently_signed_prefix_survives_legitimate_audit_append_and_role_changes()
    {
        using var f = new StoreFixture(); using var signer = RSA.Create(2048); using var verifier = Public(signer);
        var count = f.Scalar("SELECT count(*) FROM audit");
        var anchor = f.Store.CreateAuditAnchor(f.Customer, "auditor", signer, "audit-key-v1");
        Assert.Equal(count, anchor.ThroughSequence); Assert.Equal(count + 1, f.Scalar("SELECT count(*) FROM audit"));
        Assert.Equal("RSA-PSS-SHA256", anchor.Algorithm); Assert.Equal("audit-prefix", anchor.Kind);
        Assert.Equal(f.Customer, anchor.CustomerId); Assert.Equal(f.Cell, anchor.CellId);
        Assert.True(FoundationAuditAnchors.VerifySignature(anchor, verifier, "audit-key-v1", f.Clock.GetUtcNow()));
        Assert.True(f.Store.VerifyAuditAnchor(f.Customer, "auditor", anchor, verifier, "audit-key-v1"));
        _ = f.Plan();
        f.Store.SetMembership(f.Customer, "admin", "requester", "default", "dev", ["Requester", "Viewer"]);
        Assert.True(f.Store.VerifyAuditAnchor(f.Customer, "auditor", anchor, verifier, "audit-key-v1"));
        Assert.Equal(FoundationAuditAnchors.PublicKeyFingerprint(verifier), anchor.PublicKeySha256);
    }

    [Fact]
    public void Recomputing_the_entire_local_hash_chain_cannot_rewrite_an_independently_held_signed_anchor()
    {
        using var f = new StoreFixture(); using var key = RSA.Create(2048);
        var anchor = f.Store.CreateAuditAnchor(f.Customer, "auditor", key, "held-key");
        var rows = f.Store.ListAudit(f.Customer, "auditor", limit: 1000);
        f.Execute("DROP TRIGGER audit_no_update");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Path, Pooling = false }.ToString()); db.Open();
        var previous = "";
        foreach (var original in rows)
        {
            var row = original.Sequence == 2 ? original with { Reason = "privileged-rewrite" } : original;
            var canonical = JsonSerializer.Serialize(new
            {
                sequence = row.Sequence, customer = row.CustomerId, cell = row.CellId, eventId = row.EventId,
                actor = row.ActorKey, project = row.ProjectId, environment = row.EnvironmentId, action = row.Action,
                target = row.TargetId, outcome = row.Outcome, reason = row.Reason, correlation = row.CorrelationId,
                created = row.CreatedAt.ToUnixTimeMilliseconds(), previous
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            using var command = db.CreateCommand(); command.CommandText = "UPDATE audit SET reason=$reason,previous_hash=$previous,event_hash=$hash WHERE sequence=$sequence";
            command.Parameters.AddWithValue("$reason", row.Reason); command.Parameters.AddWithValue("$previous", previous);
            command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$sequence", row.Sequence); command.ExecuteNonQuery();
            previous = hash;
        }
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, "auditor")); // The local administrator has repaired every local hash.
        Assert.True(FoundationAuditAnchors.VerifySignature(anchor, key, "held-key", f.Clock.GetUtcNow()));
        Assert.False(f.Store.VerifyAuditAnchor(f.Customer, "auditor", anchor, key, "held-key"));
    }

    [Fact]
    public void Database_truncation_or_unsigned_prefix_corruption_fails_anchor_verification()
    {
        using var f = new StoreFixture(); using var key = RSA.Create(2048);
        var anchor = f.Store.CreateAuditAnchor(f.Customer, "auditor", key, "audit-key");
        f.Execute("DROP TRIGGER audit_no_delete; DELETE FROM audit WHERE sequence>=" + anchor.ThroughSequence);
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, "auditor"));
        Assert.False(f.Store.VerifyAuditAnchor(f.Customer, "auditor", anchor, key, "audit-key"));
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("actor")]
    [InlineData("role-version")]
    [InlineData("sequence")]
    [InlineData("hash")]
    [InlineData("time")]
    [InlineData("schema")]
    [InlineData("kind")]
    [InlineData("key-id")]
    [InlineData("fingerprint")]
    [InlineData("algorithm")]
    [InlineData("signature")]
    public void Every_identity_scope_prefix_time_version_and_key_field_is_bound_by_the_signature(string field)
    {
        using var f = new StoreFixture(); using var key = RSA.Create(2048);
        var a = f.Store.CreateAuditAnchor(f.Customer, "auditor", key, "audit-key");
        a = field switch
        {
            "customer" => a with { CustomerId = "other-customer" }, "cell" => a with { CellId = "other-cell" },
            "project" => a with { ProjectId = "other-project" }, "environment" => a with { EnvironmentId = "prod" },
            "actor" => a with { CreatedBy = "other-auditor" }, "role-version" => a with { SignedByRoleVersion = a.SignedByRoleVersion + 1 },
            "sequence" => a with { ThroughSequence = a.ThroughSequence + 1 }, "hash" => a with { PrefixHash = new('a', 64) },
            "time" => a with { CreatedUtc = a.CreatedUtc.AddSeconds(1) }, "schema" => a with { Version = 2 },
            "kind" => a with { Kind = "backup-manifest" }, "key-id" => a with { KeyId = "other-key" },
            "fingerprint" => a with { PublicKeySha256 = new('b', 64) }, "algorithm" => a with { Algorithm = "RS256" },
            _ => a with { Signature = Convert.ToBase64String(new byte[256]) }
        };
        Assert.False(FoundationAuditAnchors.VerifySignature(a, key, "audit-key", f.Clock.GetUtcNow()));
        Assert.False(f.Store.VerifyAuditAnchor(f.Customer, "auditor", a, key, "audit-key"));
    }

    [Fact]
    public void Public_key_pin_wrong_key_or_wrong_algorithm_cannot_validate_an_anchor()
    {
        using var f = new StoreFixture(); using var key = RSA.Create(2048); using var wrong = RSA.Create(2048);
        var a = f.Store.CreateAuditAnchor(f.Customer, "auditor", key, "key-one");
        Assert.False(FoundationAuditAnchors.VerifySignature(a, wrong, "key-one", f.Clock.GetUtcNow()));
        Assert.False(FoundationAuditAnchors.VerifySignature(a, key, "key-two", f.Clock.GetUtcNow()));
        var body = typeof(FoundationAuditAnchors).GetMethod("Payload", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [a]) as byte[];
        var legacy = a with { Signature = Convert.ToBase64String(key.SignData(body!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
        Assert.False(FoundationAuditAnchors.VerifySignature(legacy, key, "key-one", f.Clock.GetUtcNow()));
    }

    [Fact]
    public void Weak_or_public_only_signing_keys_never_create_an_anchor_audit_event()
    {
        using var f = new StoreFixture(); using var weak = RSA.Create(1024); using var privateKey = RSA.Create(2048); using var publicOnly = Public(privateKey);
        var before = f.Scalar("SELECT count(*) FROM audit");
        foreach (var key in new[] { weak, publicOnly })
            Assert.Equal("AuditAnchorSigningKeyInvalid", Assert.Throws<FoundationStoreException>(() => f.Store.CreateAuditAnchor(f.Customer, "auditor", key, "key")).Code);
        Assert.Equal(before, f.Scalar("SELECT count(*) FROM audit"));
        Assert.Equal("AuditAnchorSigningKeyInvalid", Assert.Throws<FoundationStoreException>(() => f.Store.CreateAuditAnchor(f.Customer, "auditor", privateKey, "key\n")).Code);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("requester")]
    [InlineData("operator")]
    [InlineData("admin")]
    public void Anchor_creation_requires_current_auditor_grant_and_cannot_use_general_administration(string actor)
    {
        using var f = new StoreFixture(); using var key = RSA.Create(2048);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.CreateAuditAnchor(f.Customer, actor, key, "key")).Code);
    }

    [Fact]
    public void Customer_scope_current_verifier_membership_and_machine_actor_limits_are_enforced()
    {
        using var f = new StoreFixture(); using var key = RSA.Create(2048);
        var a = f.Store.CreateAuditAnchor(f.Customer, "auditor", key, "key");
        Assert.Equal("CustomerScopeDenied", Assert.Throws<FoundationStoreException>(() => f.Store.CreateAuditAnchor("other", "auditor", key, "key")).Code);
        Assert.Equal("CustomerScopeDenied", Assert.Throws<FoundationStoreException>(() => f.Store.VerifyAuditAnchor("other", "auditor", a, key, "key")).Code);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.VerifyAuditAnchor(f.Customer, "viewer", a, key, "key")).Code);
        f.Store.SetMembership(f.Customer, "admin", "workload:synthetic", "default", "dev", ["Auditor"]);
        Assert.Equal("HumanPrincipalRequired", Assert.Throws<FoundationStoreException>(() => f.Store.CreateAuditAnchor(f.Customer, "workload:synthetic", key, "key")).Code);
        f.Store.SetMembership(f.Customer, "admin", "auditor", "default", "dev", [], enabled: false);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.VerifyAuditAnchor(f.Customer, "auditor", a, key, "key")).Code);
    }

    [Fact]
    public void Anchor_age_is_explicit_policy_and_historical_signatures_do_not_silently_expire()
    {
        using var f = new StoreFixture(); using var key = RSA.Create(2048);
        var a = f.Store.CreateAuditAnchor(f.Customer, "auditor", key, "key");
        f.Clock.Advance(TimeSpan.FromDays(2));
        Assert.True(f.Store.VerifyAuditAnchor(f.Customer, "auditor", a, key, "key"));
        Assert.False(f.Store.VerifyAuditAnchor(f.Customer, "auditor", a, key, "key", maximumAge: TimeSpan.FromHours(1)));
        Assert.False(FoundationAuditAnchors.VerifySignature(a, key, "key", f.Clock.GetUtcNow(), TimeSpan.Zero));
        Assert.False(FoundationAuditAnchors.VerifySignature(a, key, "key", a.CreatedUtc.AddMinutes(-6)));
        Assert.False(FoundationAuditAnchors.VerifySignature(a with { CreatedUtc = a.CreatedUtc.AddTicks(1) }, key, "key", f.Clock.GetUtcNow()));
    }

    [Fact]
    public void New_anchor_files_are_verified_atomic_and_nonoverwriting()
    {
        using var f = new StoreFixture(); using var key = RSA.Create(2048); using var directory = new AnchorDirectory();
        var a = f.Store.CreateAuditAnchor(f.Customer, "auditor", key, "key");
        var files = new FoundationAuditAnchorFiles(directory.Path, f.Customer, f.Cell, f.Clock);
        var path = files.WriteNew(a, key, "key");
        Assert.Equal(a, files.ReadVerified(a.AnchorId, key, "key"));
        Assert.True(f.Store.VerifyAuditAnchor(f.Customer, "auditor", files.ReadVerified(a.AnchorId, key, "key"), key, "key"));
        Assert.Throws<IOException>(() => files.WriteNew(a, key, "key"));
        Assert.Single(Directory.GetFiles(directory.Path)); Assert.Equal(a, files.ReadVerified(a.AnchorId, key, "key"));
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("changed-content")]
    [InlineData("duplicate-field")]
    [InlineData("unknown-field")]
    [InlineData("too-large")]
    public void Tampered_ambiguous_or_unbounded_anchor_files_fail_closed(string fault)
    {
        using var f = new StoreFixture(); using var key = RSA.Create(2048); using var directory = new AnchorDirectory();
        var a = f.Store.CreateAuditAnchor(f.Customer, "auditor", key, "key");
        var files = new FoundationAuditAnchorFiles(directory.Path, f.Customer, f.Cell, f.Clock); var path = files.WriteNew(a, key, "key");
        var text = File.ReadAllText(path);
        if (fault == "changed-content")
        {
            var doc = JsonNode.Parse(text)!.AsObject(); doc["prefixHash"] = new string('c', 64); text = doc.ToJsonString();
        }
        else if (fault == "duplicate-field") text = text.TrimEnd('}') + ",\"Version\":1}";
        else if (fault == "unknown-field") text = text.TrimEnd('}') + ",\"privateKey\":\"forbidden\"}";
        else text = new string('x', 32769);
        File.WriteAllText(path, text);
        Assert.Throws<FoundationStoreException>(() => files.ReadVerified(a.AnchorId, key, "key"));
    }

    [Fact]
    public void Linked_anchor_roots_and_destinations_are_rejected_without_touching_the_link_target()
    {
        using var f = new StoreFixture(); using var key = RSA.Create(2048); using var directory = new AnchorDirectory();
        var target = System.IO.Path.Combine(directory.Path, "target"); Directory.CreateDirectory(target);
        File.WriteAllText(System.IO.Path.Combine(target, "preserved.txt"), "synthetic target remains intact");
        var link = System.IO.Path.Combine(directory.Path, "linked-root");
        CreateDirectoryLink(link, target);
        try
        {
            Assert.Equal("AuditAnchorLinkedPathDenied", Assert.Throws<FoundationStoreException>(() =>
                new FoundationAuditAnchorFiles(link, f.Customer, f.Cell, f.Clock)).Code);
        }
        finally { Directory.Delete(link, recursive: false); }
        var root = System.IO.Path.Combine(directory.Path, "anchors");
        var files = new FoundationAuditAnchorFiles(root, f.Customer, f.Cell, f.Clock);
        var anchor = f.Store.CreateAuditAnchor(f.Customer, "auditor", key, "key");
        var destination = System.IO.Path.Combine(root, anchor.AnchorId + ".anchor.json");
        CreateDirectoryLink(destination, target);
        try
        {
            Assert.Equal("AuditAnchorLinkedPathDenied", Assert.Throws<FoundationStoreException>(() => files.WriteNew(anchor, key, "key")).Code);
            Assert.Equal("synthetic target remains intact", File.ReadAllText(System.IO.Path.Combine(target, "preserved.txt")));
            Assert.Single(Directory.GetFiles(target));
        }
        finally { Directory.Delete(destination, recursive: false); }
    }

    [Fact]
    public void File_ownership_and_identifier_paths_cannot_be_retargeted_to_another_cell_or_directory()
    {
        using var f = new StoreFixture(); using var key = RSA.Create(2048); using var directory = new AnchorDirectory();
        var a = f.Store.CreateAuditAnchor(f.Customer, "auditor", key, "key");
        var other = new FoundationAuditAnchorFiles(directory.Path, "other-customer", "other-cell", f.Clock);
        Assert.Equal("AuditAnchorSignatureInvalid", Assert.Throws<FoundationStoreException>(() => other.WriteNew(a, key, "key")).Code);
        var files = new FoundationAuditAnchorFiles(directory.Path, f.Customer, f.Cell, f.Clock);
        foreach (var id in new[] { "../outside", "C:\\outside", a.AnchorId + "/other", new string('A', 32) })
            Assert.Equal("AuditAnchorIdInvalid", Assert.Throws<FoundationStoreException>(() => files.ReadVerified(id, key, "key")).Code);
        Assert.Throws<ArgumentException>(() => new FoundationAuditAnchorFiles("relative/path", f.Customer, f.Cell));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    static RSA Public(RSA source) { var key = RSA.Create(); key.ImportParameters(source.ExportParameters(false)); return key; }
    static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10000) && process.ExitCode == 0, "Synthetic junction creation failed: " + output + error);
    }
    sealed class AnchorDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PlatformStudio-AuditAnchors", Guid.NewGuid().ToString("N"));
        public AnchorDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            var parent = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PlatformStudio-AuditAnchors"));
            if (!resolved.StartsWith(parent + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("Fixture cleanup escaped its owned directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
