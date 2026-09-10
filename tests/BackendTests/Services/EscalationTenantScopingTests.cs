using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Escalation had no tenant isolation of any kind: EscalationService took no ITenantScope and
/// EscalationController filtered nothing. On a policy gated only by Schedule.Read that meant
/// every tenant's policies and events — with Employee and Shift included, so another
/// customer's staff and rota — were readable by anyone.
///
/// Worse, acknowledging is a WRITE that suppresses every remaining tier, so a Schedule.Write
/// holder in one tenant could silence another tenant's unanswered escalation. That is the
/// safety-critical half: not a disclosure, a way to stop somebody else's clinician being paged.
/// </summary>
public class EscalationTenantScopingTests
{
    private const int MineTenant = 1;
    private const int TheirsTenant = 2;
    private const int MinePolicy = 10;
    private const int TheirsPolicy = 20;
    private const int MineEvent = 100;
    private const int TheirsEvent = 200;

    private static AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        db.Tenants.AddRange(
            new Tenant { Id = MineTenant, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow },
            new Tenant { Id = TheirsTenant, Name = "North Campus", IsActive = true, CreatedAt = DateTime.UtcNow });

        // A policy reaches its tenant through its department, the same way a phone tree does.
        db.Departments.AddRange(
            new Department { Id = 1, Name = "Cardiology", TenantId = MineTenant },
            new Department { Id = 2, Name = "Neurology", TenantId = TheirsTenant });

        db.EscalationPolicies.AddRange(
            new EscalationPolicy { Id = MinePolicy, Name = "Mine", DepartmentId = 1, IsActive = true },
            new EscalationPolicy { Id = TheirsPolicy, Name = "Theirs", DepartmentId = 2, IsActive = true });

        // EscalationEvent.EmployeeId and .ShiftId are required relationships, so the Include
        // chain in GetEventsAsync joins away any event whose employee or shift is missing.
        // Seeding them is not ceremony: an event that references neither is not a shape the
        // engine can produce.
        var mineEmployee = Person("Mine", "Provider", MineTenant);
        var theirsEmployee = Person("Theirs", "Provider", TheirsTenant);
        db.Employees.AddRange(mineEmployee, theirsEmployee);

        db.Schedules.AddRange(
            new Schedule { Id = 1, Name = "Cardiology call", DepartmentId = 1 },
            new Schedule { Id = 2, Name = "Neurology call", DepartmentId = 2 });

        var now = DateTime.UtcNow;
        db.Shifts.AddRange(
            Duty(1, 1, mineEmployee.Id, now),
            Duty(2, 2, theirsEmployee.Id, now));

        db.EscalationEvents.AddRange(
            new EscalationEvent
            {
                Id = MineEvent, PolicyId = MinePolicy, EmployeeId = mineEmployee.Id, ShiftId = 1,
                Tier = 1, Status = "pending", TriggeredAt = now,
            },
            new EscalationEvent
            {
                Id = TheirsEvent, PolicyId = TheirsPolicy, EmployeeId = theirsEmployee.Id, ShiftId = 2,
                Tier = 1, Status = "pending", TriggeredAt = now,
            });

        db.SaveChanges();
        return db;
    }

    private static Employee Person(string first, string last, int tenantId) => new()
    {
        Id = Guid.NewGuid(),
        FirstName = first,
        LastName = last,
        Email = $"{first.ToLowerInvariant()}@example.test",
        TenantId = tenantId,
        IsActive = true,
    };

    private static Shift Duty(int id, int scheduleId, Guid employeeId, DateTime now) => new()
    {
        Id = id,
        ScheduleId = scheduleId,
        EmployeeId = employeeId,
        Tier = "primary",
        Status = "scheduled",
        StartTime = now.AddHours(-1),
        EndTime = now.AddHours(7),
    };

    private static EscalationService Service(AppDbContext db, ITenantScope scope) =>
        new(db, NullLogger<EscalationService>.Instance, scope);

    [Fact]
    public async Task AScopedCallerSeesOnlyTheirOwnPolicies()
    {
        using var db = NewDb();

        var policies = await Service(db, TestTenantScopes.For(MineTenant)).GetPoliciesAsync();

        policies.Select(p => p.Id).Should().BeEquivalentTo(new[] { MinePolicy });
    }

    [Fact]
    public async Task AScopedCallerSeesOnlyTheirOwnEvents()
    {
        // Events carry Employee and Shift, so an unscoped read hands over another customer's
        // staff and rota, not merely the fact that an escalation happened.
        using var db = NewDb();

        var events = await Service(db, TestTenantScopes.For(MineTenant)).GetEventsAsync();

        events.Select(e => e.Id).Should().BeEquivalentTo(new[] { MineEvent });
    }

    [Fact]
    public async Task AScopedCallerCannotAcknowledgeAnotherTenantsEscalation()
    {
        using var db = NewDb();
        var service = Service(db, TestTenantScopes.For(MineTenant));

        // KeyNotFoundException, not a forbid: whether another customer holds an event with a
        // given id is itself something they should not learn. It maps to 404.
        var act = () => service.AcknowledgeEventAsync(TheirsEvent);
        await act.Should().ThrowAsync<KeyNotFoundException>();

        // And the suppression must not have happened.
        var theirs = await db.EscalationEvents.AsNoTracking().FirstAsync(e => e.Id == TheirsEvent);
        theirs.Status.Should().Be("pending");
        theirs.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public async Task AScopedCallerCanStillAcknowledgeTheirOwn()
    {
        using var db = NewDb();

        var acked = await Service(db, TestTenantScopes.For(MineTenant)).AcknowledgeEventAsync(MineEvent);

        acked.Status.Should().Be("resolved");
        acked.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AScopedCallerCannotDeleteAnotherTenantsPolicy()
    {
        using var db = NewDb();
        var service = Service(db, TestTenantScopes.For(MineTenant));

        var act = () => service.DeletePolicyAsync(TheirsPolicy);
        await act.Should().ThrowAsync<KeyNotFoundException>();

        (await db.EscalationPolicies.AsNoTracking().AnyAsync(p => p.Id == TheirsPolicy)).Should().BeTrue();
    }

    [Fact]
    public async Task TheBackgroundEngineIsNotRestricted()
    {
        // The regression that would matter most: scoping the engine itself would silently stop
        // escalation for the entire estate, and a stopped engine looks exactly like a quiet
        // night. Unrestricted is what TenantScope returns with no request context.
        using var db = NewDb();

        var policies = await Service(db, TestTenantScopes.Unrestricted).GetPoliciesAsync();
        var events = await Service(db, TestTenantScopes.Unrestricted).GetEventsAsync();

        policies.Select(p => p.Id).Should().BeEquivalentTo(new[] { MinePolicy, TheirsPolicy });
        events.Select(e => e.Id).Should().BeEquivalentTo(new[] { MineEvent, TheirsEvent });
    }

    [Fact]
    public async Task APolicyWithNoDepartmentIsNotReachableByAScopedCaller()
    {
        // A departmentless policy has no tenant, and escalates against every tenant's shifts.
        // It is the widest-scoped row there is, which is precisely the one a scoped admin has
        // least business reading or deleting — the same posture taken for a tenantless grant.
        using var db = NewDb();
        db.EscalationPolicies.Add(new EscalationPolicy
        {
            Id = 30, Name = "Global", DepartmentId = null, IsActive = true,
        });
        await db.SaveChangesAsync();

        var policies = await Service(db, TestTenantScopes.For(MineTenant)).GetPoliciesAsync();
        policies.Select(p => p.Id).Should().NotContain(30);

        var act = () => Service(db, TestTenantScopes.For(MineTenant)).DeletePolicyAsync(30);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
