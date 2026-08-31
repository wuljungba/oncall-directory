using FluentAssertions;
using Microsoft.Graph.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Entra leaves `mail` null on any cloud-only account without a mailbox, which is most
/// accounts created in the portal. Employee.Email is required and uniquely indexed, so a
/// blank cannot be stored — the sync fetched every user and imported none of them.
///
/// These pin the fallback, and the one case it deliberately refuses: a guest's UPN is
/// directory plumbing, not a way to reach anybody.
/// </summary>
public class GraphEmailResolutionTests
{
    [Fact]
    public void Mail_IsPreferred()
    {
        var user = new User
        {
            Mail = "clinician@hospital.org",
            OtherMails = ["other@hospital.org"],
            UserPrincipalName = "upn@tenant.onmicrosoft.com",
        };

        GraphApiService.ResolveEmail(user).Should().Be("clinician@hospital.org");
    }

    [Fact]
    public void OtherMails_UsedWhenMailIsMissing()
    {
        var user = new User
        {
            Mail = null,
            OtherMails = ["backup@hospital.org"],
            UserPrincipalName = "upn@tenant.onmicrosoft.com",
        };

        GraphApiService.ResolveEmail(user).Should().Be("backup@hospital.org");
    }

    [Fact]
    public void UserPrincipalName_UsedWhenNothingElseIsSet()
    {
        // The case that blocked every real sync: a cloud-only member with no mailbox.
        var user = new User
        {
            Mail = null,
            UserPrincipalName = "oncalladmin@tenant.onmicrosoft.com",
        };

        GraphApiService.ResolveEmail(user).Should().Be("oncalladmin@tenant.onmicrosoft.com");
    }

    [Fact]
    public void GuestUpn_IsRefused()
    {
        // "someone_gmail.com#EXT#@tenant.onmicrosoft.com" is an identifier, not an address.
        // Storing it would put an unreachable contact in the directory.
        var user = new User
        {
            Mail = null,
            UserPrincipalName = "someone_gmail.com#EXT#@tenant.onmicrosoft.com",
        };

        GraphApiService.ResolveEmail(user).Should().BeEmpty();
    }

    [Fact]
    public void GuestWithARealAddressRecorded_IsAccepted()
    {
        // The documented remedy: set otherMails on the guest.
        var user = new User
        {
            Mail = null,
            OtherMails = ["someone@gmail.com"],
            UserPrincipalName = "someone_gmail.com#EXT#@tenant.onmicrosoft.com",
        };

        GraphApiService.ResolveEmail(user).Should().Be("someone@gmail.com");
    }

    [Fact]
    public void NothingUsable_YieldsEmpty_SoTheCallerCanSkipIt()
    {
        GraphApiService.ResolveEmail(new User()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("  spaced@hospital.org  ", "spaced@hospital.org")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void BlankAndWhitespaceMailFallsThrough(string mail, string? expected)
    {
        var user = new User { Mail = mail, UserPrincipalName = "upn@tenant.onmicrosoft.com" };

        GraphApiService.ResolveEmail(user)
            .Should().Be(expected ?? "upn@tenant.onmicrosoft.com");
    }
}
