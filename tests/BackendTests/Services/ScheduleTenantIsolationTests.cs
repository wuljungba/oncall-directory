using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;
using ValidationException = System.ComponentModel.DataAnnotations.ValidationException;

namespace BackendTests.Services;

/// <summary>
/// Schedule writes must be tenant-scoped, not just schedule reads.
///
/// Reads went through ScopeSchedulesAsync from the start, but every write used a bare
/// FindAsync. So a scheduler at one hospital could rename, repoint, deactivate or delete
/// another hospital's on-call schedule, reassign its shifts, approve swaps on them, or
/// acknowledge them -- and the on-call schedule is what decides who gets paged for a code
/// call. Same reads-scoped-writes-unscoped shape previously found on phone trees.
///
/// These also pin the second half of the problem: an unknown schedule, shift or employee id
/// used to reach SaveChanges and come back as a raw DbUpdateException (HTTP 500) naming
/// neither the field nor the value.
/// </summary>
public class ScheduleTenantIsolationTests
{
    private const int TenantA = 1;
    private const int TenantB = 2;
    private const int DeptA = 10;
    private const int DeptB = 20;
    private const int ScheduleA = 100;
    private const int ScheduleB = 200;
    private const int ShiftB = 2000;
    private static readonly Guid EmpB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);

        db.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Main Hospital", IsActive = true },
            new Tenant { Id = TenantB, Name = "North Campus", IsActive = true });
        db.Departments.AddRange(
            new Department { Id = DeptA, Name = "Cardiology", TenantId = TenantA, IsActive = true },
            new Department { Id = DeptB, Name = "Neurology", TenantId = TenantB, IsActive = true });
        db.Employees.Add(new Employee
        {
            Id = EmpB,
            FirstName = "Ben",
            LastName = "Beta",
            Email = "ben@north.example",
            AzureAdObjectId = "oid-b",
            DepartmentId = DeptB,
            TenantId = TenantB,
            IsActive = true,
        });
        db.Schedules.AddRange(
            new Schedule { Id = ScheduleA, Name = "Cardiology call", DepartmentId = DeptA, IsActive = true },
            new Schedule { Id = ScheduleB, Name = "Neurology call", DepartmentId = DeptB, IsActive = true });

        var now = DateTime.UtcNow;
        db.Shifts.Add(new Shift
        {
            Id = ShiftB,
            ScheduleId = ScheduleB,
            EmployeeId = EmpB,
            Tier = "primary",
            Status = "scheduled",
            StartTime = now.AddHours(-1),
            EndTime = now.AddHours(1),
        });
        db.SaveChanges();
        return db;
    }

    /// <summary>A caller who may only touch tenant A.</summary>
    private static ScheduleService ServiceForTenantA(AppDbContext db) =>
        new(db, NullLogger<ScheduleService>.Instance, TestTenantScopes.For(TenantA), null,
            Options.Create(new SchedulingOptions { TimeZone = "America/New_York" }));

    // ── Cross-tenant writes ──

    [Fact]
    public async Task CannotRenameAnotherTenantsSchedule()
    {
        using var db = CreateDb();
        var service = ServiceForTenantA(db);

        var act = () => service.UpdateScheduleAsync(new Schedule
        {
            Id = ScheduleB,
            Name = "Hijacked",
            DepartmentId = DeptB,
            IsActive = false,
        });

        await act.Should().ThrowAsync<KeyNotFoundException>();

        var untouched = await db.Schedules.AsNoTracking().FirstAsync(s => s.Id == ScheduleB);
        untouched.Name.Should().Be("Neurology call");
        untouched.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CannotDeleteAnotherTenantsSchedule()
    {
        using var db = CreateDb();
        var service = ServiceForTenantA(db);

        var act = () => service.DeleteScheduleAsync(ScheduleB);

        await act.Should().ThrowAsync<KeyNotFoundException>();
        db.Schedules.Any(s => s.Id == ScheduleB).Should().BeTrue();
    }

    [Fact]
    public async Task CannotAssignAShiftOnAnotherTenantsSchedule()
    {
        using var db = CreateDb();
        var service = ServiceForTenantA(db);
        var before = db.Shifts.Count();

        var act = () => service.AssignShiftAsync(
            ScheduleB, EmpB, DateTime.UtcNow, DateTime.UtcNow.AddHours(8), "primary");

        await act.Should().ThrowAsync<KeyNotFoundException>();
        db.Shifts.Count().Should().Be(before, "a refused assignment must not create a shift");
    }

    [Fact]
    public async Task CannotGenerateShiftsOnAnotherTenantsSchedule()
    {
        using var db = CreateDb();
        var service = ServiceForTenantA(db);

        var act = () => service.GenerateShiftsAsync(ScheduleB, weeks: 1);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    /// <summary>
    /// Acknowledging is the signal the escalation engine trusts, so silencing another
    /// tenant's shift must be impossible. This method reports "not found" as null.
    /// </summary>
    [Fact]
    public async Task CannotAcknowledgeAnotherTenantsShift()
    {
        using var db = CreateDb();
        var service = ServiceForTenantA(db);

        var result = await service.AcknowledgeShiftAsync(ShiftB, EmpB, isAdmin: true);

        result.Should().BeNull();
        var untouched = await db.Shifts.AsNoTracking().FirstAsync(s => s.Id == ShiftB);
        untouched.Status.Should().Be("scheduled");
    }

    [Fact]
    public async Task CannotRequestASwapOnAnotherTenantsShift()
    {
        using var db = CreateDb();
        var service = ServiceForTenantA(db);

        var act = () => service.RequestSwapAsync(ShiftB, EmpB, null, "cover me");

        await act.Should().ThrowAsync<KeyNotFoundException>();
        db.ShiftSwaps.Any().Should().BeFalse();
    }

    [Fact]
    public async Task CannotCreateAScheduleInAnotherTenantsDepartment()
    {
        using var db = CreateDb();
        var service = ServiceForTenantA(db);

        var act = () => service.CreateScheduleAsync(new Schedule
        {
            Name = "Planted",
            DepartmentId = DeptB,
            IsActive = true,
        });

        await act.Should().ThrowAsync<ValidationException>();
        db.Schedules.Any(s => s.Name == "Planted").Should().BeFalse();
    }

    // ── Unknown ids are a clean error, not a database-level explosion ──

    [Fact]
    public async Task AssigningToAnUnknownEmployeeIsAValidationError()
    {
        using var db = CreateDb();
        var service = ServiceForTenantA(db);

        var act = () => service.AssignShiftAsync(
            ScheduleA, Guid.NewGuid(), DateTime.UtcNow, DateTime.UtcNow.AddHours(8), "primary");

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task CreatingAScheduleWithNoDepartmentIsAValidationError()
    {
        using var db = CreateDb();
        var service = ServiceForTenantA(db);

        var act = () => service.CreateScheduleAsync(new Schedule { Name = "Orphan", IsActive = true });

        await act.Should().ThrowAsync<ValidationException>();
    }

    // ── The legitimate case still works ──

    [Fact]
    public async Task CanStillManageYourOwnTenantsSchedule()
    {
        using var db = CreateDb();
        var service = ServiceForTenantA(db);

        var updated = await service.UpdateScheduleAsync(new Schedule
        {
            Id = ScheduleA,
            Name = "Cardiology call (revised)",
            DepartmentId = DeptA,
            IsActive = true,
        });

        updated.Name.Should().Be("Cardiology call (revised)");
    }
}
