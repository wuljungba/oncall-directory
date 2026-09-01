using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OnCallApi.Authentication;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Opening self-signup changes the application's posture: it was invite-only, and the
/// only way to exist here was for an administrator to create you.
///
/// The property that has to survive that change is simple to state and easy to break:
/// signing up proves you can be reached at an address, and grants nothing. Everything
/// below is a way that could stop being true.
/// </summary>
public class SelfSignupTests
{
    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static LocalAccountService CreateService(AppDbContext db, params string[] superAdminEmails)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Local:SigningKey"] = "test-signing-key-long-enough-for-hmac-sha256",
            })
            .Build();

        var jwt = new LocalJwtService(
            config, NullLogger<LocalJwtService>.Instance,
            new StubHostEnvironment { EnvironmentName = Environments.Development });

        return new LocalAccountService(
            db, jwt,
            Options.Create(new SuperAdminOptions { Emails = superAdminEmails.ToList() }),
            NullLogger<LocalAccountService>.Instance);
    }

    private const string GoodPassword = "correct horse battery staple";

    // ── What signing up gets you ──

    [Fact]
    public async Task SigningUpGrantsNothingAtAll()
    {
        using var db = CreateDb();

        var account = await CreateService(db).RegisterSelfServeAsync(
            "newcomer@example.com", GoodPassword, "Newcomer");

        account.Roles.Should().BeEmpty("an account somebody made for themselves speaks for nobody");
        account.EmployeeId.Should().BeNull();
        account.Origin.Should().Be(LocalAccountOrigin.SelfSignup);

        (await db.PermissionGrants.CountAsync()).Should().Be(0,
            "the staff baseline is for accounts an administrator vouched for");
    }

    /// <summary>
    /// The admin path keeps its behaviour. If this ever fails, the two paths have been
    /// merged and the wrong one won.
    /// </summary>
    [Fact]
    public async Task AnAdminCreatedAccountStillGetsTheStaffBaseline()
    {
        using var db = CreateDb();

        var account = await CreateService(db).RegisterAsync(
            "hired@example.com", GoodPassword, "New Hire");

        account.Origin.Should().Be(LocalAccountOrigin.Admin);
        (await db.PermissionGrants.CountAsync()).Should().Be(1);
    }

    // ── Addresses that already mean somebody ──

    /// <summary>
    /// The escalation this closes: TenantClaimsMiddleware resolves permission grants BY
    /// EMAIL. An administrator later granting access to jane@hospital.example -- meaning
    /// the real Jane, who signs in with Microsoft -- would be granting it to whoever had
    /// registered that address here first.
    /// </summary>
    [Fact]
    public async Task AnAddressAlreadyInTheDirectoryCannotBeClaimed()
    {
        using var db = CreateDb();
        db.Employees.Add(new Employee
        {
            Id = Guid.NewGuid(),
            AzureAdObjectId = "real-jane",
            FirstName = "Jane",
            LastName = "Smith",
            Email = "jane@hospital.example",
        });
        await db.SaveChangesAsync();

        var signup = async () => await CreateService(db).RegisterSelfServeAsync(
            "JANE@hospital.example", GoodPassword, "Not Jane");

        await signup.Should().ThrowAsync<InvalidOperationException>();
        (await db.LocalAccounts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AnAddressThatAlreadyHoldsAGrantCannotBeClaimed()
    {
        using var db = CreateDb();
        db.PermissionGrants.Add(new PermissionGrant
        {
            PrincipalType = "external",
            ExternalPrincipalId = "future.hire@hospital.example",
            Permissions = "Schedule.Read",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var signup = async () => await CreateService(db).RegisterSelfServeAsync(
            "future.hire@hospital.example", GoodPassword, "Someone Else");

        await signup.Should().ThrowAsync<InvalidOperationException>();
        (await db.LocalAccounts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ASuperAdminAddressCannotBeClaimed()
    {
        using var db = CreateDb();

        var signup = async () => await CreateService(db, "boss@hospital.example")
            .RegisterSelfServeAsync("boss@hospital.example", GoodPassword, "Not The Boss");

        await signup.Should().ThrowAsync<InvalidOperationException>();
        (await db.LocalAccounts.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// Every refusal reads the same. Different messages would turn the signup form into a
    /// way to enumerate which addresses belong to staff, which hold permissions, and who
    /// the super admins are -- one submission at a time.
    /// </summary>
    [Fact]
    public async Task EveryRefusalSaysTheSameThing()
    {
        using var db = CreateDb();
        db.Employees.Add(new Employee
        {
            Id = Guid.NewGuid(), AzureAdObjectId = "e", FirstName = "Jane", LastName = "Smith",
            Email = "in.directory@hospital.example",
        });
        db.PermissionGrants.Add(new PermissionGrant
        {
            PrincipalType = "external", ExternalPrincipalId = "has.grant@hospital.example",
            Permissions = "Schedule.Read", IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = CreateService(db, "boss@hospital.example");
        await service.RegisterSelfServeAsync("taken@example.com", GoodPassword, "First");

        var messages = new List<string>();
        foreach (var address in new[]
                 {
                     "in.directory@hospital.example",
                     "has.grant@hospital.example",
                     "boss@hospital.example",
                     "taken@example.com",
                 })
        {
            try
            {
                await service.RegisterSelfServeAsync(address, GoodPassword, "Someone");
                throw new Exception($"{address} should have been refused");
            }
            catch (InvalidOperationException ex)
            {
                messages.Add(ex.Message);
            }
        }

        messages.Distinct().Should().HaveCount(1,
            "a refusal that explains itself is an oracle for what exists here");
    }

    [Fact]
    public async Task AnUnclaimedAddressIsAccepted()
    {
        using var db = CreateDb();

        var account = await CreateService(db).RegisterSelfServeAsync(
            "  Nobody@Example.COM ", GoodPassword, "Nobody");

        account.Email.Should().Be("nobody@example.com", "the address is normalized once, on the way in");
    }

    // ── Lockout ──

    [Fact]
    public async Task RepeatedWrongPasswordsLockTheAccount()
    {
        using var db = CreateDb();
        var service = CreateService(db);
        await service.RegisterSelfServeAsync("newcomer@example.com", GoodPassword, "Newcomer");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var (failed, _) = await service.AuthenticateAsync("newcomer@example.com", "wrong");
            failed.Should().BeNull();
        }

        // The right password, now that the account is locked.
        var (account, token) = await service.AuthenticateAsync("newcomer@example.com", GoodPassword);

        account.Should().BeNull("the lockout has to cost the attempt, not just the answer");
        token.Should().BeNull();

        (await db.LocalAccounts.SingleAsync()).LockedOutUntil.Should().NotBeNull();
    }

    [Fact]
    public async Task ASuccessfulSignInClearsTheFailureCount()
    {
        using var db = CreateDb();
        var service = CreateService(db);
        await service.RegisterSelfServeAsync("newcomer@example.com", GoodPassword, "Newcomer");

        await service.AuthenticateAsync("newcomer@example.com", "wrong");
        await service.AuthenticateAsync("newcomer@example.com", "wrong");

        var (account, token) = await service.AuthenticateAsync("newcomer@example.com", GoodPassword);
        account.Should().NotBeNull();
        token.Should().NotBeNull();

        var stored = await db.LocalAccounts.SingleAsync();
        stored.FailedLoginCount.Should().Be(0,
            "five wrong attempts spread over a month are somebody mistyping, not somebody guessing");
        stored.LockedOutUntil.Should().BeNull();
    }

    /// <summary>
    /// A self-registered account can sign in -- it just cannot do anything. The token
    /// carries no roles, so every permission-gated endpoint refuses it.
    /// </summary>
    [Fact]
    public async Task ASelfRegisteredAccountSignsInWithNoRoles()
    {
        using var db = CreateDb();
        var service = CreateService(db);
        await service.RegisterSelfServeAsync("newcomer@example.com", GoodPassword, "Newcomer");

        var (account, token) = await service.AuthenticateAsync("newcomer@example.com", GoodPassword);

        account.Should().NotBeNull();
        token.Should().NotBeNull();
        account!.Roles.Should().BeEmpty();
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
