using SelfService.Portal;
using Xunit;
namespace SelfService.Portal.Tests;

public sealed class ResourceIdentityTests
{
    const string Group="/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/CaseKept";
    [Theory]
    [InlineData("", "CaseKept", "Microsoft.Resources/resourceGroups")]
    [InlineData("/providers/Microsoft.Storage/storageAccounts/MyStore", "MyStore", "Microsoft.Storage/storageAccounts")]
    [InlineData("/providers/Microsoft.Storage/storageAccounts/MyStore/blobServices/default/containers/data", "MyStore/default/data", "Microsoft.Storage/storageAccounts/blobServices/containers")]
    [InlineData("/providers/Microsoft.Storage/storageAccounts/MyStore/providers/Microsoft.Insights/diagnosticSettings/audit", "audit", "Microsoft.Insights/diagnosticSettings")]
    public void PreservesReturnedNamesAndNestedTypes(string suffix,string fullName,string type)
    {
        var result=ResourceIdentity.Parse(Group+suffix);
        Assert.Equal(fullName,result.FullName);Assert.Equal(type,result.Type);
        if(suffix.Contains("/containers/"))Assert.EndsWith("/blobServices/default",result.ParentId);
        if(suffix.Contains("/diagnosticSettings/"))Assert.EndsWith("/storageAccounts/MyStore",result.ParentId);
    }
    [Theory][InlineData("/")][InlineData("/providers/Microsoft.Storage/storageAccounts")][InlineData("/providers/Microsoft.Storage/storageAccounts/a?query")][InlineData("/../bad")]
    public void RejectsMalformedReturnedIdentities(string suffix)=>Assert.Throws<PortalException>(()=>ResourceIdentity.Parse(Group+suffix));
}
