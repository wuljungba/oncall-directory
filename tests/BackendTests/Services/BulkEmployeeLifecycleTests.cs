using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OnCallApi.Authorization;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Bulk lifecycle actions, and the promises they make about access.
///
/// Two of these matter more than the rest. A batch is routinely part-blocked, because anyone who
/// has ever held a shift cannot be hard-deleted — so the response has to say which records were
/// refused rather than failing all of them. And because permission grants are keyed by address
/// rather than by employee, removing a person has to remove their access too, or the grant
/// silently waits for whoever next signs in with that address.
/// </summary>
public class BulkEmployeeLifecycleTests
{
    private static readonly Guid AliceId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid BobId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid CarolId = Guid.Parse("20000000-0000-0000-0000-000000000003");
    private static readonly Guid UnitId = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private static readonly Guid AdminEmpId = Guid.Parse("10000000-0000-0000-0000-000000000005");

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        SeedData(db);
        return db;
    }

    private static void SeedData(AppDbContext db)
    {
        db.Tenants.Add(new Tenant { Id = 1, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.Tenants.Add(new Tenant { Id = 2, Name = "North Campus", IsActive = true, CreatedAt = DateTime.UtcNow });

        db.Employees.Add(new Employee
        {
            Id = AliceId, AzureAdObjectId = "tenant1-user-a", FirstName = "Alice", LastName = "Adams",
            Email = "alice@main.test", TenantId = 1, IsActive = true, Source = "CsvImport",
        });
        // Reports to Alice, so a blocked delete of Alice must not orphan Bob's manager link.
        db.Employees.Add(new Employee
        {
            Id = BobId, AzureAdObjectId = "tenant1-user-b", FirstName = "Bob", LastName = "Baker",
            Email = "bob@main.test", TenantId = 1, IsActive = true, ManagerId = AliceId, Source = "CsvImport",
        });
        db.Employees.Add(new Employee
        {
            Id = CarolId, AzureAdObjectId = "tenant2-user-c", FirstName = "Carol", LastName = "Clark",
            Email = "carol@north.test", TenantId = 2, IsActive = true, Source = "Local",
        });
        // A unit line: a label and a number, never a sign-in identity.
        db.Employees.Add(new Employee
        {
            Id = UnitId, AzureAdObjectId = "csv-import-abc123", FirstName = "", LastName = "",
            DisplayName = "3North", Email = null, TenantId = 1, IsActive = true,
            ContactType = "Department", Source = "CsvImport",
        });
        db.Employees.Add(new Employee
        {
            Id = AdminEmpId, AzureAdObjectId = "admin-tenant1", FirstName = "Dana", LastName = "Doyle",
            Email = "dana@main.test", TenantId = 1, IsActive = true, Source = "Local",
        });

        db.TenantAdmins.Add(new TenantAdmin
        {
            Id = 1, TenantId = 1, AzureAdObjectId = "admin-tenant1",
            Role = "DepartmentAdmin", IsAutoAssigned = false, CreatedAt = DateTime.UtcNow,
        });

        db.SaveChanges();
    }

    private static AdminService CreateService(AppDbContext db, ClaimsPrincipal user)
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns(new DefaultHttpContext { User = user });

        return new AdminService(
            db,
            NullLogger<AdminService>.Instance,
            new TenantContextService(db, accessor.Object),
            accessor.Object,
            new Mock<IAuditService>().Object);
    }

    private static ClaimsPrincipal SuperAdmin() => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, "superadmin-oid"),
        new Claim(Permissions.ClaimType, Permissions.AdminFull),
    ], "test-auth"));

    /// <summary>An administrator of tenant 1 only, via the seeded TenantAdmin row.</summary>
    private static ClaimsPrincipal SubAdmin(string? employeeIdClaim = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "admin-tenant1"),
            new("oid", "admin-tenant1"),
            new(Permissions.ClaimType, Permissions.AdminScoped),
        };
        if (employeeIdClaim != null) claims.Add(new Claim("employee_id", employeeIdClaim));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test-auth"));
    }

    private static PermissionGrant Grant(string principal, int? tenantId, string perms = "Schedule.Read") => new()
    {
        TenantId = tenantId,
        PrincipalType = "external",
        ExternalPrincipalId = principal,
        Permissions = perms,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
    };

    // ── Tenant isolation ──

    [Fact]
    public async Task BulkDeactivate_SubAdmin_SkipsAnotherTenantsEmployeeAndDoesNotNameThem()
    {
        var db = CreateDbContext();
        var service = CreateService(db, SubAdmin());

        var result = await service.BulkDeactivateEmployeesAsync([AliceId, CarolId], null, false);

        result.Results.Single(r => r.EmployeeId == AliceId).Outcome.Should().Be(BulkOutcomes.Succeeded);

        var carol = result.Results.Single(r => r.EmployeeId == CarolId);
        carol.Outcome.Should().Be(BulkOutcomes.NotFound);

        // The single-record path answers 404 rather than 403 for another tenant's employee so
        // that existence is never confirmed. Echoing a name back here would undo that.
        carol.DisplayName.Should().BeNull("bulk must not confirm that another tenant's record exists");
        carol.Email.Should().BeNull();

        (await db.Employees.FindAsync(CarolId))!.IsActive.Should().BeTrue();
    }

    // ── Partial success ──

    [Fact]
    public async Task BulkDelete_OneEmployeeHasShiftHistory_TheOthersAreStillDeleted()
    {
        var db = CreateDbContext();
        db.Shifts.Add(new Shift
        {
            Id = 1, ScheduleId = 1, EmployeeId = AliceId,
            StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(8),
        });
        db.SaveChanges();

        var service = CreateService(db, SuperAdmin());

        var result = await service.BulkDeleteEmployeesAsync([AliceId, BobId], null, true);

        // NOTE: the in-memory provider does not enforce foreign keys, so this passes only
        // because the reference pre-flight exists. It is the regression test for removing it.
        var alice = result.Results.Single(r => r.EmployeeId == AliceId);
        alice.Outcome.Should().Be(BulkOutcomes.BlockedByHistory);
        alice.Message.Should().Contain("shifts");

        result.Results.Single(r => r.EmployeeId == BobId).Outcome.Should().Be(BulkOutcomes.Succeeded);
        result.Succeeded.Should().Be(1);
        result.Blocked.Should().Be(1);

        (await db.Employees.FindAsync(AliceId)).Should().NotBeNull();
        (await db.Employees.FindAsync(BobId)).Should().BeNull();
    }

    [Fact]
    public async Task BulkDelete_BlockedEmployee_KeepsTheirPermissionGrant()
    {
        var db = CreateDbContext();
        db.Shifts.Add(new Shift
        {
            Id = 1, ScheduleId = 1, EmployeeId = AliceId,
            StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(8),
        });
        db.PermissionGrants.Add(Grant("alice@main.test", 1));
        db.SaveChanges();

        var service = CreateService(db, SuperAdmin());

        await service.BulkDeleteEmployeesAsync([AliceId], null, true);

        // Revoking and deleting commit together. Stripping a still-present employee's access
        // while reporting the delete failed would be the worst of both.
        db.PermissionGrants.Single().IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task BulkDelete_BlockedEmployee_KeepsTheirDirectReportsManagerLink()
    {
        var db = CreateDbContext();
        db.Shifts.Add(new Shift
        {
            Id = 1, ScheduleId = 1, EmployeeId = AliceId,
            StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddHours(8),
        });
        db.SaveChanges();

        var service = CreateService(db, SuperAdmin());

        await service.BulkDeleteEmployeesAsync([AliceId], null, true);

        // Detaching reports for the whole batch up front would null this permanently, for a
        // delete that never happened.
        (await db.Employees.FindAsync(BobId))!.ManagerId.Should().Be(AliceId);
    }

    // ── Grant revocation ──

    [Fact]
    public async Task BulkDeactivate_RevokesTheGrantKeyedToTheirEmail_WithoutDeletingTheRow()
    {
        var db = CreateDbContext();
        db.PermissionGrants.Add(Grant("ALICE@MAIN.TEST", 1));
        db.SaveChanges();

        var service = CreateService(db, SubAdmin());

        var result = await service.BulkDeactivateEmployeesAsync([AliceId], null, false);

        result.Results.Single().GrantsRevoked.Should().Be(1);

        // Switched off, not destroyed: once the person is gone this row is the only record that
        // the address ever held these permissions, and both resolvers filter on IsActive.
        var grant = db.PermissionGrants.Single();
        grant.IsActive.Should().BeFalse();
        grant.Permissions.Should().Be("Schedule.Read");
    }

    [Fact]
    public async Task BulkDeactivate_AlsoRevokesAGrantKeyedToTheirDirectoryObjectId()
    {
        var db = CreateDbContext();
        db.PermissionGrants.Add(Grant("tenant1-user-a", 1));
        db.SaveChanges();

        var service = CreateService(db, SubAdmin());

        var result = await service.BulkDeactivateEmployeesAsync([AliceId], null, false);

        // The middleware honours object-id grants. Revoking only the email would leave working
        // access behind while reporting it removed.
        result.Results.Single().GrantsRevoked.Should().Be(1);
        db.PermissionGrants.Single().IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task BulkDeactivate_DoesNotMatchASyntheticImporterObjectId()
    {
        var db = CreateDbContext();
        db.PermissionGrants.Add(Grant("csv-import-abc123", 1));
        db.SaveChanges();

        var service = CreateService(db, SubAdmin());

        var result = await service.BulkDeactivateEmployeesAsync([UnitId], null, false);

        // No token ever presents a csv-import id, so such a grant confers nothing. Counting it
        // as revoked would report access removed that never existed.
        result.Results.Single().GrantsRevoked.Should().Be(0);
        db.PermissionGrants.Single().IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task BulkDeactivate_RecordWithNoEmail_SucceedsAndSaysWhyNothingWasRevoked()
    {
        var db = CreateDbContext();
        var service = CreateService(db, SubAdmin());

        var result = await service.BulkDeactivateEmployeesAsync([UnitId], null, false);

        var item = result.Results.Single();
        item.Outcome.Should().Be(BulkOutcomes.Succeeded, "the lifecycle change really did happen");
        item.GrantsRevoked.Should().Be(0);
        item.Message.Should().ContainEquivalentOf("no email");
    }

    [Fact]
    public async Task BulkDeactivate_SubAdmin_LeavesASystemWideGrantAloneAndReportsIt()
    {
        var db = CreateDbContext();
        db.PermissionGrants.Add(Grant("alice@main.test", tenantId: null));
        db.SaveChanges();

        var service = CreateService(db, SubAdmin());

        var result = await service.BulkDeactivateEmployeesAsync([AliceId], null, false);

        // A system-wide grant reaches every subscription; an administrator of one must not be
        // able to destroy it as a side effect of tidying their own directory.
        db.PermissionGrants.Single().IsActive.Should().BeTrue();
        result.SystemWideGrantsLeft.Should().Be(1);
        result.Results.Single().Message.Should().Contain("system-wide");
    }

    [Fact]
    public async Task BulkDeactivate_SuperAdmin_DoesRevokeASystemWideGrant()
    {
        var db = CreateDbContext();
        db.PermissionGrants.Add(Grant("alice@main.test", tenantId: null));
        db.SaveChanges();

        var service = CreateService(db, SuperAdmin());

        var result = await service.BulkDeactivateEmployeesAsync([AliceId], null, false);

        db.PermissionGrants.Single().IsActive.Should().BeFalse();
        result.SystemWideGrantsLeft.Should().Be(0);
    }

    [Fact]
    public async Task BulkReactivate_DoesNotRestoreRevokedGrants()
    {
        var db = CreateDbContext();
        var grant = Grant("alice@main.test", 1);
        grant.IsActive = false;
        db.PermissionGrants.Add(grant);
        var alice = db.Employees.Find(AliceId)!;
        alice.IsActive = false;
        db.SaveChanges();

        var service = CreateService(db, SubAdmin());

        var result = await service.BulkReactivateEmployeesAsync([AliceId], null, false);

        result.Results.Single().Outcome.Should().Be(BulkOutcomes.Succeeded);
        (await db.Employees.FindAsync(AliceId))!.IsActive.Should().BeTrue();

        // The chosen behaviour, and the reason the UI must say so before deactivating.
        db.PermissionGrants.Single().IsActive.Should().BeFalse();
        result.Results.Single().Message.Should().Contain("not restored");
    }

    // ── Guards ──

    [Fact]
    public async Task BulkDeactivate_SkipsTheCallersOwnRecord()
    {
        var db = CreateDbContext();
        var service = CreateService(db, SubAdmin(employeeIdClaim: AliceId.ToString()));

        var result = await service.BulkDeactivateEmployeesAsync([AliceId, BobId], null, false);

        result.Results.Single(r => r.EmployeeId == AliceId).Outcome.Should().Be(BulkOutcomes.SkippedSelf);
        (await db.Employees.FindAsync(AliceId))!.IsActive.Should().BeTrue();
        result.Results.Single(r => r.EmployeeId == BobId).Outcome.Should().Be(BulkOutcomes.Succeeded);
    }

    [Fact]
    public async Task BulkDeactivate_FlagsAnAdministratorAndLeavesTheirAdminRowIntact()
    {
        var db = CreateDbContext();
        var service = CreateService(db, SuperAdmin());

        var result = await service.BulkDeactivateEmployeesAsync([AdminEmpId], null, false);

        var item = result.Results.Single();
        item.Outcome.Should().Be(BulkOutcomes.Succeeded);

        // Admin rights come from a TenantAdmin row, which no grant revocation touches. Saying
        // "access revoked" without flagging this would be false.
        item.IsPrivilegedPrincipal.Should().BeTrue();
        result.PrivilegedPrincipals.Should().Be(1);

        // Never removed as a side effect — that is how a subscription ends up with no admin.
        db.TenantAdmins.Should().ContainSingle(a => a.AzureAdObjectId == "admin-tenant1");
    }

    [Fact]
    public async Task BulkDelete_RefusesWhenTheBatchHoldsAnAdministratorAndItWasNotAcknowledged()
    {
        var db = CreateDbContext();
        var service = CreateService(db, SuperAdmin());

        var act = async () => await service.BulkDeleteEmployeesAsync([AdminEmpId], null, acknowledgePrivileged: false);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.Employees.FindAsync(AdminEmpId)).Should().NotBeNull();
    }

    [Fact]
    public async Task Bulk_RefusesABatchLargerThanTheCap()
    {
        var db = CreateDbContext();
        var service = CreateService(db, SuperAdmin());
        var tooMany = Enumerable.Range(0, BulkLimits.MaxBatch + 1).Select(_ => Guid.NewGuid()).ToList();

        var act = async () => await service.BulkDeactivateEmployeesAsync(tooMany, null, false);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ── Audit ──

    [Fact]
    public async Task BulkDelete_WritesOneAuditRowPerEmployeeSharingABatchId()
    {
        var db = CreateDbContext();
        var service = CreateService(db, SuperAdmin());

        var result = await service.BulkDeleteEmployeesAsync([BobId, CarolId], "cleanup", true);

        // Readable at all only because these are written through the DbContext rather than
        // enqueued: IAuditService is mocked here, and the real one is a bounded channel that
        // drops entries under load.
        var rows = db.AuditLogs.Where(a => a.ResourceType == "Employee" && a.Action == "Deleted").ToList();
        rows.Should().HaveCount(2);
        rows.Select(r => r.ResourceId).Should().BeEquivalentTo([BobId.ToString(), CarolId.ToString()]);
        rows.Should().OnlyContain(r => r.Details!.Contains(result.BatchId.ToString()));
        rows.Should().OnlyContain(r => r.PrincipalId == "superadmin-oid");

        // Plus one row for the request itself, so a batch that changed nothing is still recorded.
        db.AuditLogs.Should().ContainSingle(a => a.Action == "BulkDelete" && a.ResourceId == null);
    }

    // ── Removing access, not just permissions ──

    [Fact]
    public async Task BulkDeactivate_AlsoSwitchesOffTheirLocalSignInAccount()
    {
        var db = CreateDbContext();
        db.LocalAccounts.Add(new LocalAccount
        {
            Id = 1, Email = "alice@main.test", PasswordHash = "x", DisplayName = "Alice",
            EmployeeId = AliceId, IsActive = true,
        });
        db.SaveChanges();

        var service = CreateService(db, SubAdmin());

        var result = await service.BulkDeactivateEmployeesAsync([AliceId], null, false);

        // Revoking grants alone leaves a working credential: LocalAccount has its own IsActive
        // and no foreign key to the employee, so it survives a delete and still authenticates.
        db.LocalAccounts.Single().IsActive.Should().BeFalse();
        result.Results.Single().SignInsDisabled.Should().Be(1);
        result.SignInsDisabled.Should().Be(1);
    }

    [Fact]
    public async Task BulkDeactivate_MatchesASignInAccountByAddressWhenItIsNotLinked()
    {
        var db = CreateDbContext();
        // No EmployeeId: nothing constrains the two to stay in step, so the address has to be
        // matched as well or an unlinked account keeps working.
        db.LocalAccounts.Add(new LocalAccount
        {
            Id = 1, Email = "ALICE@MAIN.TEST", PasswordHash = "x", DisplayName = "Alice",
            EmployeeId = null, IsActive = true,
        });
        db.SaveChanges();

        var service = CreateService(db, SubAdmin());

        await service.BulkDeactivateEmployeesAsync([AliceId], null, false);

        db.LocalAccounts.Single().IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task BulkReactivate_DoesNotSwitchTheSignInAccountBackOn()
    {
        var db = CreateDbContext();
        db.LocalAccounts.Add(new LocalAccount
        {
            Id = 1, Email = "alice@main.test", PasswordHash = "x", DisplayName = "Alice",
            EmployeeId = AliceId, IsActive = false,
        });
        var alice = db.Employees.Find(AliceId)!;
        alice.IsActive = false;
        db.SaveChanges();

        var service = CreateService(db, SubAdmin());

        await service.BulkReactivateEmployeesAsync([AliceId], null, false);

        // Same reasoning as permissions: restoring the directory record does not restore access.
        db.LocalAccounts.Single().IsActive.Should().BeFalse();
    }

    // ── A refusal is itself an event worth recording ──

    [Fact]
    public async Task BulkDelete_RefusedForAnUnacknowledgedAdmin_StillRecordsTheAttempt()
    {
        var db = CreateDbContext();
        var service = CreateService(db, SuperAdmin());

        var act = async () => await service.BulkDeleteEmployeesAsync([AdminEmpId], "tidy up", false);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // The summary row at the end of a run cannot cover this — the refusal throws before it.
        // An attempt to mass-delete administrator records must not vanish without trace.
        var row = db.AuditLogs.Should().ContainSingle(a => a.Action == "BulkDeleteRefused").Subject;
        row.Details.Should().Contain("Privileged=1");
        row.PrincipalId.Should().Be("superadmin-oid");
    }

    [Fact]
    public async Task Bulk_RefusedForAnOversizedBatch_StillRecordsTheAttempt()
    {
        var db = CreateDbContext();
        var service = CreateService(db, SuperAdmin());
        var tooMany = Enumerable.Range(0, BulkLimits.MaxBatch + 1).Select(_ => Guid.NewGuid()).ToList();

        var act = async () => await service.BulkDeactivateEmployeesAsync(tooMany, null, false);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();

        db.AuditLogs.Should().ContainSingle(a => a.Action == "BulkDeactivateRefused");
    }

    [Fact]
    public async Task BulkDelete_DoesNotDemandAcknowledgementForTheCallersOwnAdminRecord()
    {
        var db = CreateDbContext();
        // The caller IS the administrator in the batch, and their own record is always skipped —
        // so asking them to confirm it would be asking about something never acted on.
        var service = CreateService(db, SubAdmin(employeeIdClaim: AdminEmpId.ToString()));

        var result = await service.BulkDeleteEmployeesAsync([AdminEmpId], null, acknowledgePrivileged: false);

        result.Results.Single().Outcome.Should().Be(BulkOutcomes.SkippedSelf);
    }
}
