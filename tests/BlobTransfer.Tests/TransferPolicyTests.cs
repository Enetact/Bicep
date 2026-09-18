using System.Text.Json;
using Xunit;
namespace BlobTransfer.Tests;
public class TransferPolicyTests
{
    private static readonly PipelineOptions Options = new(new() { ["claims/"] = "claims", ["claims/special/"] = "special", [""] = "default" });
    private static readonly Uri Source = new("https://configured.blob.core.windows.net/incoming");
    [Fact]
    public void StableRequestIsDerivedFromConfiguredSourceNameAndRevision()
    {
        var work = new WorkItem(1, "arbitrary report.pdf", "\"etag-1\"");
        Assert.Equal(TransferPolicy.Key(work, Source, Options), TransferPolicy.Key(work, Source, Options));
        Assert.Equal(TransferPolicy.Key(work, Source, Options), TransferPolicy.Key(work with { SourceETag = "etag-1" }, Source, Options));
        Assert.NotEqual(TransferPolicy.Key(work, Source, Options), TransferPolicy.Key(work with { SourceETag = "etag-2" }, Source, Options));
        Assert.NotEqual(TransferPolicy.Key(work, Source, Options), TransferPolicy.Key(work, new Uri("https://other.blob.core.windows.net/incoming"), Options));
    }
    [Theory]
    [InlineData("claims/report.pdf", "claims")]
    [InlineData("claims/special/report.pdf", "special")]
    [InlineData("plain.csv", "default")]
    public void ScopeComesFromServerPrefixMapping(string name, string expected) =>
        Assert.Equal(expected, TransferPolicy.ResolveScope(name, Options));
    [Fact]
    public void UnmappedPrefixIsRejected() =>
        Assert.Throws<PermanentTransferException>(() => TransferPolicy.ResolveScope("unknown/file", new(new() { ["known/"] = "known" })));
    [Fact]
    public void UnknownMessageVersionIsRejected() =>
        Assert.Throws<PermanentTransferException>(() => TransferPolicy.ParseMessage(JsonSerializer.Serialize(new WorkItem(2, "file", "etag"))));
    [Fact]
    public void DestinationKeySeparatesScopes()
    {
        var hash = TransferPolicy.HashText("same bytes");
        Assert.NotEqual(new TransferKey("scope-a", hash).DestinationName(hash), new TransferKey("scope-b", hash).DestinationName(hash));
    }
    [Fact]
    public void ReconciliationRespectsRetryAndQuarantine()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(TransferPolicy.IsDue(null, now, 24));
        Assert.False(TransferPolicy.IsDue(new() { Status = "Quarantined" }, now, 24));
        Assert.False(TransferPolicy.IsDue(new() { Status = "Retry", NextAttemptUtc = now.AddMinutes(1) }, now, 24));
        Assert.True(TransferPolicy.IsDue(new() { Status = "Processing", NextAttemptUtc = now.AddMinutes(-1) }, now, 24));
        Assert.False(TransferPolicy.IsDue(new() { Status = "Completed", VerifiedUtc = now }, now, 24));
        Assert.True(TransferPolicy.IsDue(new() { Status = "Completed", VerifiedUtc = now.AddDays(-2) }, now, 24));
    }
}
