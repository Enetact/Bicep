using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;
using SelfService.Foundation;

namespace SelfService.Foundation.Tests;

public sealed class CredentialRestrictedRecoveryCoreTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public void General_v2_signature_canonical_bytes_match_the_frozen_pre_profile_wire()
    {
        var time = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        var ns = new CredentialVaultNamespaceIdentity("10000000-0000-0000-0000-000000000001", "20000000-0000-0000-0000-000000000001",
            new string('1', 32), new string('a', 64), new string('2', 32), new string('b', 64));
        var signature = new CredentialScopedRecoverySetSignature(2, "credential-scoped-recovery-set", new string('d', 32), ns.CustomerId, ns.CellId,
            ns, 7, time, time, time, time.AddDays(1), new string('e', 64), 0, 0, new string('f', 64), 2, "core-signer", new string('c', 64), "RSA-PSS-SHA256", "");
        const string expected = """
{"version":2,"kind":"credential-scoped-recovery-set","setId":"dddddddddddddddddddddddddddddddd","customerId":"10000000-0000-0000-0000-000000000001","cellId":"20000000-0000-0000-0000-000000000001","sourceNamespace":{"customerId":"10000000-0000-0000-0000-000000000001","cellId":"20000000-0000-0000-0000-000000000001","vaultNamespaceId":"11111111111111111111111111111111","namespaceCommitmentSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","authorityNamespaceId":"22222222222222222222222222222222","authorityBindingSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},"generation":7,"capturedUnixMilliseconds":1792886400000,"issuedUnixMilliseconds":1792886400000,"notBeforeUnixMilliseconds":1792886400000,"expiresUnixMilliseconds":1792972800000,"inventorySha256":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","profileCount":0,"absenceProofCount":0,"contentSha256":"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff","contentLength":2,"keyId":"core-signer","publicKeySha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","algorithm":"RSA-PSS-SHA256"}
""";
        var actual = (byte[])typeof(CredentialScopedRecoverySets).GetMethod("CanonicalPayload", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [signature])!;
        Assert.Equal(Encoding.UTF8.GetBytes(expected), actual); Assert.DoesNotContain("accessProfile", JsonSerializer.Serialize(signature, Json));
    }

}
