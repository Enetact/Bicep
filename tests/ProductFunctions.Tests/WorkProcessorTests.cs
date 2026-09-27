using ProductFunctions;
using Xunit;

public class WorkProcessorTests
{
    sealed class Store : IReceiptStore
    {
        readonly Dictionary<string, WorkReceipt> rows = new();
        public Task<StoreResult> Put(WorkReceipt r, CancellationToken ct) => Task.FromResult(rows.TryAdd(r.Id, r) ? StoreResult.Created : rows[r.Id] == r ? StoreResult.Duplicate : StoreResult.Conflict);
    }
    [Fact] public async Task RedeliveryIsIdempotentAndChangedContentConflicts()
    {
        var store = new Store(); var id = Guid.NewGuid(); var body = $"{{\"id\":\"{id}\",\"operation\":\"record\"}}";
        Assert.Equal(StoreResult.Created, await WorkProcessor.Process(body, store, default));
        Assert.Equal(StoreResult.Duplicate, await WorkProcessor.Process(body, store, default));
        Assert.Equal(StoreResult.Conflict, await WorkProcessor.Process(body + " ", store, default));
    }
    [Theory]
    [InlineData("{}")] [InlineData("[]")] [InlineData("bad")]
    [InlineData("{\"id\":\"not-an-id\",\"operation\":\"record\"}")]
    [InlineData("{\"id\":\"00000000-0000-0000-0000-000000000000\",\"operation\":\"record\"}")]
    [InlineData("{\"id\":\"e5f7cab5-0082-41ad-8a66-51f2f2247767\",\"operation\":\"execute\"}")]
    [InlineData("{\"id\":\"e5f7cab5-0082-41ad-8a66-51f2f2247767\",\"operation\":\"record\",\"script\":\"anything\"}")]
    public void InvalidOrUnexpectedInputRejected(string body) => Assert.Throws<InvalidWorkException>(() => WorkProcessor.Parse(body));
    [Fact] public void OversizeRejected() => Assert.Throws<InvalidWorkException>(() => WorkProcessor.Parse(new string('x',16385)));
    sealed class FailingStore : IReceiptStore { public Task<StoreResult> Put(WorkReceipt r, CancellationToken ct) => throw new IOException("retry"); }
    [Fact] public async Task TransientFailurePropagates() => await Assert.ThrowsAsync<IOException>(() => WorkProcessor.Process("{\"id\":\"e5f7cab5-0082-41ad-8a66-51f2f2247767\",\"operation\":\"record\"}",new FailingStore(),default));
}
