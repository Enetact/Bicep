using Xunit;

namespace BlobTransfer.Tests;

public class CopyPolicyTests
{
    [Fact]
    public void ChangedRevisionOrSourceCannotMatchCompletedCopy()
    {
        var source = new Uri("https://example.blob.core.windows.net/incoming/a.pdf");
        var first = CopyPolicy.Fingerprint(source, "etag-1");
        Assert.NotEqual(first, CopyPolicy.Fingerprint(source, "etag-2"));
        Assert.NotEqual(first, CopyPolicy.Fingerprint(new Uri("https://example.blob.core.windows.net/incoming/b.pdf"), "etag-1"));
        Assert.Equal(first, CopyPolicy.Fingerprint(source, "etag-1"));
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(101, 100)]
    [InlineData(0, 0)]
    public void InvalidSizesFailClosed(long size, long maximum) =>
        Assert.Throws<InvalidOperationException>(() => CopyPolicy.ValidateLength(size, maximum));

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void EmptyAndMaximumSizedFilesAreAllowed(long size) => CopyPolicy.ValidateLength(size, 100);

    [Fact]
    public void OnlyMatchingCommittedRevisionAndLengthIsAnIdempotentSuccess()
    {
        var metadata = new Dictionary<string, string> { ["sourcefingerprint"] = "revision-a" };
        Assert.True(CopyPolicy.IsCompletedDuplicate(metadata, "revision-a", 100, 100));
        Assert.False(CopyPolicy.IsCompletedDuplicate(metadata, "revision-b", 100, 100));
        Assert.False(CopyPolicy.IsCompletedDuplicate(metadata, "revision-a", 99, 100));
        Assert.False(CopyPolicy.IsCompletedDuplicate(new Dictionary<string, string>(), "revision-a", 100, 100));
    }
}
