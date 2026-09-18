using System.Reflection;
using Microsoft.Azure.Functions.Worker;
using Xunit;

namespace BlobTransfer.Tests;

public class FunctionContractTests
{
    [Fact]
    public void ShippingAssemblyContainsOnlyTheFiveQueuePipelineFunctions()
    {
        var functions = typeof(QueueFunctions).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods())
            .Select(method => (Method: method, Function: method.GetCustomAttribute<FunctionAttribute>()))
            .Where(entry => entry.Function is not null).ToArray();
        Assert.Equal(5, functions.Length);
        Assert.Equal(5, functions.Select(entry => entry.Function!.Name).Distinct(StringComparer.Ordinal).Count());
        var expected = new Dictionary<string, Type>
        {
            ["DispatchUploadedBlob"] = typeof(BlobTriggerAttribute),
            ["CopyUploadedBlob"] = typeof(QueueTriggerAttribute),
            ["ReconcileTransfers"] = typeof(TimerTriggerAttribute),
            ["MonitorTransferPoison"] = typeof(TimerTriggerAttribute),
            ["AuditTransferLedger"] = typeof(TimerTriggerAttribute)
        };
        foreach (var (method, function) in functions)
        {
            Assert.True(expected.TryGetValue(function!.Name, out var triggerType));
            var triggers = method.GetParameters().SelectMany(parameter => parameter.GetCustomAttributes())
                .Where(attribute => attribute is BlobTriggerAttribute or QueueTriggerAttribute or TimerTriggerAttribute);
            Assert.Equal(triggerType, Assert.Single(triggers).GetType());
        }
    }
}
