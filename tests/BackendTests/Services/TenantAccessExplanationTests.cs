using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// GetAuthorizedTenantIdsAsync unions four independent rules and returns a bare list of ids,
/// so an over-broad answer gave no clue which rule produced it — diagnosing the last incident
/// meant sweeping every endpoint as the affected user. These pin the attribution that replaces
/// that, and in particular that a tenantless grant is called out as reaching every tenant.
/// </summary>
public class TenantAccessExplanationTests
{
    private static AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        db.Tenants.AddRange(
            new Tenant { Id = 1, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Tenant { Id = 2, Name = "North Campus", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Tenant { Id = 3, Name = "Closed Site", IsActive = false, CreatedAt = DateTime.UtcNow });
        db.SaveChanges();
        return db;
    }

    private static TenantContextService NewService(AppDbContext db) =>
        new(db, new Mock<IHttpContextAccessor>().Object);

    [Fact]
    public async Task ATenantlessGrantIsReportedAsReachingEveryActiveTenant()
    {
        using var db = NewDb();
        const string oid = "scoped-admin-oid";

        db.TenantAdmins.Add(new TenantAdmin
        {
            TenantId = 1, AzureAdObjectId = oid, Role = "DepartmentAdmin", CreatedAt = DateTime.UtcNow,
        });
        db.PermissionGrants.Add(new PermissionGrant
        {
            TenantId = null, // the widest grant there is
            PrincipalType = "external",
            ExternalPrincipalId = oid,
            Permissions = "Schedule.Read,Directory.Read",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var result = await NewService(db).ExplainTenantAccessAsync(oid);

        // Both rules named, so the extra tenants are attributable to the grant rather than
        // to the admin row that legitimately scopes this person to tenant 1.
        result.Sources.Should().Contain(s => s.Source == "TenantAdmin" && s.TenantIds.Contains(1));
        result.Sources.Should().Contain(s =>
            s.Source == "PermissionGrant" && s.TenantIds.Contains(1) && s.TenantIds.Contains(2));

        // Inactive tenant 3 is excluded; a deactivated subscription must not stay reachable.
        result.EffectiveTenantIds.Should().BeEquivalentTo(new[] { 1, 2 });
        result.IsConfiguredSuperAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task AScopedGrantContributesOnlyItsOwnTenant()
    {
        using var db = NewDb();
        const string oid = "well-scoped-oid";

        db.TenantAdmins.Add(new TenantAdmin
        {
            TenantId = 1, AzureAdObjectId = oid, Role = "DepartmentAdmin", CreatedAt = DateTime.UtcNow,
        });
        db.PermissionGrants.Add(new PermissionGrant
        {
            TenantId = 1,
            PrincipalType = "external",
            ExternalPrincipalId = oid,
            Permissions = "Schedule.Read",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var result = await NewService(db).ExplainTenantAccessAsync(oid);

        result.EffectiveTenantIds.Should().BeEquivalentTo(new[] { 1 });
    }

    [Fact]
    public async Task TheConnectedDirectoryRuleIsReportedButNotCounted()
    {
        using var db = NewDb();
        var tenant = await db.Tenants.FirstAsync(t => t.Id == 2);
        tenant.AzureAdTenantId = "e9577a81-c4d6-4124-984f-78f3c0efcaf4";
        await db.SaveChangesAsync();

        // A Google or local account carries no tid at all, so whether this rule applies
        // cannot be settled without the principal's own token.
        var result = await NewService(db).ExplainTenantAccessAsync("someone@gmail.test");

        result.Sources.Should().Contain(s => s.Source == "ConnectedDirectory" && s.Conditional);
        result.EffectiveTenantIds.Should().BeEmpty(
            "a conditional rule must not be counted as access the principal definitely has");
    }

    [Fact]
    public async Task AnAddressNeverMatchesATenantAdminRow()
    {
        using var db = NewDb();
        const string email = "person@hospital.test";

        // TenantAdmin rows are keyed on object id. A row whose id column holds an address
        // can only ever match a principal presenting that same string as an object id.
        db.TenantAdmins.Add(new TenantAdmin
        {
            TenantId = 1, AzureAdObjectId = email, Role = "DepartmentAdmin", CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var result = await NewService(db).ExplainTenantAccessAsync(email);

        result.Sources.Should().NotContain(s => s.Source == "TenantAdmin");
        result.EffectiveTenantIds.Should().BeEmpty();
    }
}
