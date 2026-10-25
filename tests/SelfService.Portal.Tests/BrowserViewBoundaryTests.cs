using Microsoft.Identity.Client;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class BrowserViewBoundaryTests
{
    [Fact]
    public void View_marker_is_session_private_stable_and_tracks_subject_even_with_same_display_name()
    {
        using var first = new BrowserSession(new()); using var second = new BrowserSession(new());
        Assert.NotEqual(first.ViewIdentityBoundary, second.ViewIdentityBoundary);
        var anonymous = first.ViewIdentityBoundary; first.Account = new Account("subject-a");
        var signed = first.ViewIdentityBoundary; Assert.NotEqual(anonymous, signed); Assert.Equal(signed, first.ViewIdentityBoundary);
        first.Account = new Account("subject-b"); Assert.NotEqual(signed, first.ViewIdentityBoundary);
        Assert.DoesNotContain("subject", first.ViewIdentityBoundary); Assert.Matches("^[a-f0-9]{64}$", first.ViewIdentityBoundary);
        first.Account = null; Assert.Equal(anonymous, first.ViewIdentityBoundary);
        first.AzureTenantId = "another-tenant"; Assert.NotEqual(anonymous, first.ViewIdentityBoundary);
    }
    sealed class Account(string subject) : IAccount
    {
        public string Username => "same-display@example.test";
        public string Environment => "login.microsoftonline.com";
        public AccountId HomeAccountId => new(subject + ".tenant", subject, "tenant");
    }
}
