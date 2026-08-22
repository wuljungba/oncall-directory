using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Escalation must chase shifts nobody has answered — and only those.
///
/// There was no acknowledgement mechanism at all, so the trigger reduced to "this shift has
/// been running longer than the response window". Every active shift escalated, tier after
/// tier, every two minutes, for as long as it ran: a pure alarm-fatigue generator on a
/// system whose alarms are supposed to mean something.
/// </summary>
public class EscalationTriggerTests
{
    private static readonly Guid PrimaryId = Guid.NewGuid();
    private static readonly Guid BackupId = Guid.NewGuid();

    /// <summary>Records what was sent, and whether delivery should succeed.</summary>
    private sealed class StubTeams : ITeamsNotificationService
    {
        public bool Deliver { get; set; } = true;
        public List<string> Escalations { get; } = [];

        public Task<bool> SendNotificationAsync(string userAzureAdId, string title, string message, NotificationCardType cardType = NotificationCardType.Info)
            => Task.FromResult(Deliver);

        public Task<bool> SendShiftStartingAsync(string userId, string userName, string tier, DateTime startTime, string department)
            => Task.FromResult(Deliver);

        public Task<bool> SendSwapRequestedAsync(string requesterId, string requesterName, string targetId, string targetName, string shiftInfo)
            => Task.FromResult(Deliver);

        public Task<bool> SendSwapApprovedAsync(string approverId, string requesterName, string shiftInfo)
            => Task.FromResult(Deliver);

        public Task<bool> SendGapAlertAsync(string userId, string department, DateTime gapDate)
            => Task.FromResult(Deliver);

        public Task<bool> SendEscalationAsync(string userId, string department, string tier, string details)
        {
            Escalations.Add(userId);
            return Task.FromResult(Deliver);
        }
    }

    /// <summary>An active shift that started well outside the policy window.</summary>
    private static AppDbContext CreateDb(bool acknowledged, string primaryObjectId = "primary-oid")
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);

        var now = DateTime.UtcNow;
        db.Departments.Add(new Department { Id = 1, Name = "Cardiology", IsActive = true });
        db.Employees.AddRange(
            new Employee { Id = PrimaryId, FirstName = "Pat", LastName = "Primary", Email = "pat@example.test", AzureAdObjectId = primaryObjectId, IsActive = true },
            new Employee { Id = BackupId, FirstName = "Bo", LastName = "Backup", Email = "bo@example.test", AzureAdObjectId = "backup-oid", IsActive = true });
        db.Schedules.Add(new Schedule { Id = 1, Name = "Cardiology call", DepartmentId = 1 });
        db.Shifts.Add(new Shift
        {
            Id = 1,
            ScheduleId = 1,
            EmployeeId = PrimaryId,
            Tier = "primary",
            Status = "scheduled",
            StartTime = now.AddHours(-4),
            EndTime = now.AddHours(4),
            AcknowledgedAt = acknowledged ? now.AddHours(-3) : null,
        });
        db.EscalationPolicies.Add(new EscalationPolicy
        {
            Id = 1,
            Name = "Cardiology response",
            DepartmentId = 1,
            MaxResponseMinutes = 15,
            EscalationTierCount = 3,
            IsActive = true,
        });
        db.SaveChanges();
        return db;
    }

    private static EscalationService CreateService(AppDbContext db, StubTeams teams) =>
        new(db, NullLogger<EscalationService>.Instance, teams);

    [Fact]
    public async Task UnacknowledgedShift_Escalates()
    {
        var db = CreateDb(acknowledged: false);
        var teams = new StubTeams();

        await CreateService(db, teams).CheckAndEscalateAsync();

        var events = await db.EscalationEvents.ToListAsync();
        events.Should().ContainSingle();
        events[0].Tier.Should().Be(1);
        events[0].Status.Should().Be("pending");
        teams.Escalations.Should().ContainSingle();
    }

    [Fact]
    public async Task AcknowledgedShift_DoesNotEscalate()
    {
        // The whole point: someone confirmed they have it, so there is nothing to chase.
        var db = CreateDb(acknowledged: true);
        var teams = new StubTeams();

        await CreateService(db, teams).CheckAndEscalateAsync();

        (await db.EscalationEvents.CountAsync()).Should().Be(0);
        teams.Escalations.Should().BeEmpty();
    }

    [Fact]
    public async Task UndeliveredEscalation_IsRecordedAsFailed()
    {
        // Guardrail: no best-effort delivery without an alert on failure.
        var db = CreateDb(acknowledged: false);
        var teams = new StubTeams { Deliver = false };

        await CreateService(db, teams).CheckAndEscalateAsync();

        var escalation = await db.EscalationEvents.SingleAsync();
        escalation.Status.Should().Be("notify_failed");
        escalation.Details.Should().Contain("NOT DELIVERED");
    }

    [Fact]
    public async Task TargetWithNoMicrosoftIdentity_IsRecordedAsFailedNotSkipped()
    {
        // An employee with no Entra identity cannot be reached over Teams. This branch used
        // to be skipped in silence while the escalation was still written as "pending" —
        // indistinguishable from one that had actually gone out.
        var db = CreateDb(acknowledged: false, primaryObjectId: "");
        var teams = new StubTeams();

        await CreateService(db, teams).CheckAndEscalateAsync();

        var escalation = await db.EscalationEvents.SingleAsync();
        escalation.Status.Should().Be("notify_failed");
        escalation.Details.Should().Contain("no Microsoft identity");
        teams.Escalations.Should().BeEmpty();
    }

    [Fact]
    public async Task AcknowledgingResolvesAnOpenEscalation()
    {
        var db = CreateDb(acknowledged: false);
        var teams = new StubTeams();
        await CreateService(db, teams).CheckAndEscalateAsync();
        (await db.EscalationEvents.CountAsync(e => e.Status == "pending")).Should().Be(1);

        var schedule = new ScheduleService(db, NullLogger<ScheduleService>.Instance, null);
        await schedule.AcknowledgeShiftAsync(1, PrimaryId, isAdmin: false);

        var escalation = await db.EscalationEvents.SingleAsync();
        escalation.Status.Should().Be("acknowledged");
        escalation.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AcknowledgingSomeoneElsesShift_IsRejected()
    {
        var db = CreateDb(acknowledged: false);
        var schedule = new ScheduleService(db, NullLogger<ScheduleService>.Instance, null);

        var act = () => schedule.AcknowledgeShiftAsync(1, BackupId, isAdmin: false);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await db.Shifts.SingleAsync()).AcknowledgedAt.Should().BeNull();
    }

    [Fact]
    public async Task AdminMayAcknowledgeOnSomeonesBehalf()
    {
        var db = CreateDb(acknowledged: false);
        var schedule = new ScheduleService(db, NullLogger<ScheduleService>.Instance, null);

        await schedule.AcknowledgeShiftAsync(1, BackupId, isAdmin: true);

        (await db.Shifts.SingleAsync()).AcknowledgedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AcknowledgingTwice_KeepsTheOriginalTime()
    {
        var db = CreateDb(acknowledged: false);
        var schedule = new ScheduleService(db, NullLogger<ScheduleService>.Instance, null);

        var first = await schedule.AcknowledgeShiftAsync(1, PrimaryId, isAdmin: false);
        await Task.Delay(20);
        var second = await schedule.AcknowledgeShiftAsync(1, PrimaryId, isAdmin: false);

        second!.AcknowledgedAt.Should().Be(first!.AcknowledgedAt);
    }
}
