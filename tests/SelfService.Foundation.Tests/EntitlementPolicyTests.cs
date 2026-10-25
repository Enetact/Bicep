using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class EntitlementPolicyTests
{
    [Fact]
    public void Real_pss_signature_is_bound_to_customer_cell_and_claims_and_returns_detached_features()
    {
        using var f = new EntitlementFixture(); var envelope = f.Sign();
        var result = f.Verifier.Verify(envelope, "customer-a", "cell-a");
        Assert.Equal("evaluation", result.Edition); Assert.False(result.ExternalExecutionAuthorized); Assert.False(result.IdentityGrantAuthorized);
        Assert.Contains(FoundationFeatures.ParameterizedBicep, result.Features);
        envelope.Document.Features[0] = "changed";
        Assert.Contains(FoundationFeatures.ParameterizedBicep, result.Features);
        Assert.Equal("EntitlementBindingMismatch", Error(() => f.Verifier.Verify(f.Sign(), "customer-b", "cell-a")));
        Assert.Equal("EntitlementBindingMismatch", Error(() => f.Verifier.Verify(f.Sign(), "customer-a", "cell-b")));
        var signed = f.Sign();
        Assert.Equal("EntitlementSignatureInvalid", Error(() => f.Verifier.Verify(signed with { Document = signed.Document with { Edition = "changed" } }, "customer-a", "cell-a")));
    }

    [Fact]
    public void Only_explicit_trusted_issuer_public_keys_and_pss_padding_are_accepted()
    {
        using var f = new EntitlementFixture(); using var other = RSA.Create(2048);
        Assert.Equal("EntitlementSignatureInvalid", Error(() => f.Verifier.Verify(f.Sign(key: other), "customer-a", "cell-a")));
        var document = f.Document();
        var pkcs = new SignedEntitlement(document, Convert.ToBase64String(f.Key.SignData(EntitlementVerifier.CanonicalDocument(document), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
        Assert.Equal("EntitlementSignatureInvalid", Error(() => f.Verifier.Verify(pkcs, "customer-a", "cell-a")));
        Assert.Equal("EntitlementIssuerNotTrusted", Error(() => new EntitlementVerifier([]).Verify(f.Sign(), "customer-a", "cell-a")));
        Assert.Equal("EntitlementIssuerNotTrusted", Error(() => f.Verifier.Verify(f.Sign(f.Document() with { Issuer = "different" }), "customer-a", "cell-a")));
        Assert.Equal("InvalidEntitlementTrustKey", Error(() => new EntitlementVerifier([new("issuer", "key-1", f.Key.ExportPkcs8PrivateKeyPem())])));
        var trust = new EntitlementTrustKey("issuer", "key-1", f.Key.ExportSubjectPublicKeyInfoPem());
        Assert.Equal("InvalidEntitlementTrustKey", Error(() => new EntitlementVerifier([trust, trust])));
    }

    [Theory]
    [InlineData(-1, "EntitlementExpired")]
    [InlineData(0, "EntitlementExpired")]
    [InlineData(1, null)]
    public void Expiry_is_exclusive_without_network_or_grace_period(int seconds, string? expected)
    {
        using var f = new EntitlementFixture(); var envelope = f.Sign(f.Document() with { ExpiresUtc = f.Clock.GetUtcNow().AddSeconds(seconds) });
        if (expected is null) Assert.NotNull(f.Verifier.Verify(envelope, "customer-a", "cell-a"));
        else Assert.Equal(expected, Error(() => f.Verifier.Verify(envelope, "customer-a", "cell-a")));
        Assert.Equal("EntitlementNotYetValid", Error(() => f.Verifier.Verify(f.Sign(f.Document() with { NotBefore = f.Clock.GetUtcNow().AddMinutes(1) }), "customer-a", "cell-a")));
    }

    [Fact]
    public void Unknown_duplicate_or_privileged_features_and_noncanonical_timestamps_are_rejected()
    {
        using var f = new EntitlementFixture();
        foreach (var features in new[] { new[] { "azure.deploy" }, new[] { FoundationFeatures.Administration }, new[] { FoundationFeatures.SourceCatalog, FoundationFeatures.SourceCatalog }, new[] { "SOURCE.CATALOG" } })
            Assert.Equal("InvalidEntitlementFeatures", Error(() => EntitlementVerifier.CanonicalDocument(f.Document() with { Features = features })));
        Assert.Equal("InvalidEntitlement", Error(() => EntitlementVerifier.CanonicalDocument(f.Document() with { Features = [] })));
        Assert.Equal("InvalidEntitlement", Error(() => EntitlementVerifier.CanonicalDocument(f.Document() with { Sequence = 0 })));
        Assert.Equal("InvalidEntitlement", Error(() => EntitlementVerifier.CanonicalDocument(f.Document() with { NotBefore = f.Clock.GetUtcNow().AddTicks(1) })));
        Assert.Equal("InvalidEntitlement", Error(() => EntitlementVerifier.CanonicalDocument(f.Document() with { NotBefore = f.Clock.GetUtcNow().ToOffset(TimeSpan.FromHours(1)) })));
    }

    [Fact]
    public void Raw_json_parser_rejects_unknown_missing_duplicate_and_case_ambiguous_properties()
    {
        using var f = new EntitlementFixture(); var json = JsonSerializer.Serialize(f.Sign(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(f.Sign().Document.LicenseId, EntitlementVerifier.Parse(json).Document.LicenseId);
        foreach (var invalid in new[]
        {
            json.Insert(1, "\"surprise\":true,"), json.Insert(1, "\"signature\":\"extra\","), json.Insert(1, "\"Signature\":\"extra\","),
            json.Replace("\"sequence\":1,", ""), json.Replace("\"issuer\":\"issuer\",", "\"issuer\":\"issuer\",\"ISSUER\":\"issuer\",")
        }) Assert.Equal("InvalidEntitlement", Error(() => EntitlementVerifier.Parse(invalid)));
    }

    [Fact]
    public void Feature_order_is_canonical_but_a_signature_cannot_add_a_feature()
    {
        using var f = new EntitlementFixture(); var original = f.Sign();
        var reversed = original with { Document = original.Document with { Features = original.Document.Features.Reverse().ToArray() } };
        Assert.Equal(f.Verifier.Verify(original, "customer-a", "cell-a").Digest, f.Verifier.Verify(reversed, "customer-a", "cell-a").Digest);
        var lesser = f.Sign(f.Document() with { Features = [FoundationFeatures.SourceCatalog] });
        Assert.Equal("EntitlementSignatureInvalid", Error(() => f.Verifier.Verify(lesser with { Document = f.Document() }, "customer-a", "cell-a")));
        Assert.Equal("EntitlementSignatureInvalid", Error(() => f.Verifier.Verify(original with { Signature = original.Signature + "\n" }, "customer-a", "cell-a")));
    }

    internal static string Error(Action action) => Assert.Throws<FoundationStoreException>(action).Code;
}

internal sealed class EntitlementFixture : IDisposable
{
    public RSA Key { get; } = RSA.Create(2048);
    public StoreClock Clock { get; }
    public EntitlementVerifier Verifier { get; }
    public EntitlementFixture(StoreClock? clock = null)
    {
        Clock = clock ?? new StoreClock();
        Verifier = new([new("issuer", "key-1", Key.ExportSubjectPublicKeyInfoPem())], Clock);
    }
    public EntitlementDocument Document() => new(EntitlementVerifier.DocumentSchema, "issuer", "fixture-license", "customer-a", "cell-a", 1,
        "evaluation", FoundationFeatures.Available.ToArray(), Clock.GetUtcNow().AddHours(-1), Clock.GetUtcNow().AddDays(1), "key-1");
    public SignedEntitlement Sign(EntitlementDocument? document = null, RSA? key = null)
    {
        var value = document ?? Document();
        return new(value, Convert.ToBase64String((key ?? Key).SignData(EntitlementVerifier.CanonicalDocument(value), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    }
    public void Dispose() => Key.Dispose();
}
