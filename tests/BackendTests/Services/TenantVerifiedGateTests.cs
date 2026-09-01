using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Authorization;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// The gate that stops an unverified organization writing.
///
/// It sits on the three write policies and nowhere else, so the ways it can be wrong are
/// asymmetric: refusing too much takes schedules and code calls away from a real customer,
/// while allowing too much lets an unverified organization publish an on-call roster. Both
/// are pinned here, and the refusing-too-much cases outnumber the others because that is
/// the failure that reaches patients.
/// </summary>
public class TenantVerifiedGateTests
{
    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Tenant> AddTenantAsync(AppDbContext db, string status)
    {
        var tenant = new Tenant { Name = "Test Org", IsActive = true, VerificationStatus = status };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    private static ClaimsPrincipal SignedIn() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Someone")], "test"));

    private static async Task<bool> AllowsWriteAsync(
        AppDbContext db, List<int> tenantIds, bool isSuperAdmin = false, bool scopeThrows = false)
    {
        var handler = new TenantVerifiedHandler(
            db,
            new StubTenantContext(tenantIds, isSuperAdmin, scopeThrows),
            NullLogger<TenantVerifiedHandler>.Instance);

        var requirement = new TenantVerifiedRequirement();
        var context = new AuthorizationHandlerContext([requirement], SignedIn(), null);

        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    // ── What must keep working ──

    [Fact]
    public async Task AVerifiedOrganizationMayWrite()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db, VerificationStatus.Verified);

        (await AllowsWriteAsync(db, [tenant.Id])).Should().BeTrue();
    }

    /// <summary>
    /// Much of this application predates multi-tenancy and runs with TenantId null.
    /// Refusing those writes would disable the product for every single-tenant install.
    /// </summary>
    [Fact]
    public async Task ACallerWithNoTenantScopeMayWrite()
    {
        using var db = CreateDb();

        (await AllowsWriteAsync(db, [])).Should().BeTrue();
    }

    /// <summary>
    /// Super admins are the people who approve verifications. Locking them out of an
    /// unverified organization would make the queue unworkable.
    /// </summary>
    [Fact]
    public async Task ASuperAdminMayWriteToAnUnverifiedOrganization()
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db, VerificationStatus.Unverified);

        (await AllowsWriteAsync(db, [tenant.Id], isSuperAdmin: true)).Should().BeTrue();
    }

    /// <summary>
    /// A gate that fails closed on its own internal error takes the application down for
    /// everybody. The permission check it sits alongside has already run and still applies.
    /// </summary>
    [Fact]
    public async Task TheGateFailsOpenIfItCannotResolveScope()
    {
        using var db = CreateDb();

        (await AllowsWriteAsync(db, [], scopeThrows: true)).Should().BeTrue();
    }

    /// <summary>
    /// A tenant row that is somehow missing must not block. This is the same instinct as
    /// the 'Verified' column default: absence of a decision is not a decision to refuse.
    /// </summary>
    [Fact]
    public async Task AMissingTenantRowDoesNotBlock()
    {
        using var db = CreateDb();

        (await AllowsWriteAsync(db, [4242])).Should().BeTrue();
    }

    // ── What must be refused ──

    [Theory]
    [InlineData(VerificationStatus.Unverified)]
    [InlineData(VerificationStatus.Pending)]
    [InlineData(VerificationStatus.Rejected)]
    public async Task AnOrganizationThatIsNotVerifiedMayNotWrite(string status)
    {
        using var db = CreateDb();
        var tenant = await AddTenantAsync(db, status);

        (await AllowsWriteAsync(db, [tenant.Id])).Should().BeFalse();
    }

    /// <summary>
    /// A caller scoped to several organizations is refused if ANY of them is unverified.
    /// The gate cannot see which one a given write lands on, so the safe reading is the
    /// only correct one.
    /// </summary>
    [Fact]
    public async Task OneUnverifiedOrganizationInScopeIsEnoughToRefuse()
    {
        using var db = CreateDb();
        var verified = await AddTenantAsync(db, VerificationStatus.Verified);
        var pending = await AddTenantAsync(db, VerificationStatus.Pending);

        (await AllowsWriteAsync(db, [verified.Id, pending.Id])).Should().BeFalse();
    }

    private sealed class StubTenantContext(List<int> tenantIds, bool isSuperAdmin, bool throws)
        : ITenantContextService
    {
        public Task<List<int>> GetAuthorizedTenantIdsAsync(ClaimsPrincipal user) =>
            throws
                ? Task.FromException<List<int>>(new InvalidOperationException("scope unavailable"))
                : Task.FromResult(tenantIds);

        public bool IsSuperAdmin(ClaimsPrincipal user) => isSuperAdmin;

        public Task<string?> GetUserTenantRoleAsync(ClaimsPrincipal user) => Task.FromResult<string?>(null);
        public Task<bool> IsTenantAdminAsync(ClaimsPrincipal user) => Task.FromResult(false);
        public Task<Guid?> GetCurrentEmployeeIdAsync(ClaimsPrincipal user) => Task.FromResult<Guid?>(null);
        public Task<int?> GetDepartmentTenantIdAsync(int departmentId) => Task.FromResult<int?>(null);
    }
}
