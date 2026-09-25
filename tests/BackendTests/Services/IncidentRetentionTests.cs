using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// What keeps a code-call record alive for seven years.
///
/// The record of an incident — who raised it, who was paged, how long they took, what the
/// debrief said — is the single thing a review, a coroner or a regulator asks for, and it is
/// required to be producible years later. Three separate paths could destroy one:
///
/// <list type="bullet">
/// <item>an admin-only endpoint that hard-deleted an incident outright;</item>
/// <item>deleting the code type it belonged to, which cascaded every incident under it;</item>
/// <item>a retention setting that no code read, so a value below policy did nothing at all
/// until the day a record was asked for and was gone.</item>
/// </list>
///
/// These pin all three shut.
/// </summary>
public class IncidentRetentionTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        db.Departments.Add(new Department { Id = 1, Name = "Cardiology" });
        db.SaveChanges();
        return db;
    }

    private static PhoneTreeEventService NewEventService(AppDbContext db) =>
        new(db, TestTenantScopes.Unrestricted, NullLogger<PhoneTreeEventService>.Instance);

    private static ScheduleService NewScheduleService(AppDbContext db) =>
        new(db, NullLogger<ScheduleService>.Instance, TestTenantScopes.Unrestricted);

    private static PhoneTree SeedTree(AppDbContext db, string name = "Code Blue")
    {
        var tree = new PhoneTree { Name = name, TreeType = "code-blue", DepartmentId = 1, IsActive = true };
        db.PhoneTrees.Add(tree);
        db.SaveChanges();
        return tree;
    }

    // ── Deleting the code type must not take its history ─────────────────────────────────

    [Fact]
    public async Task ACodeTypeThatHasBeenUsedCannotBeDeleted()
    {
        using var db = CreateDb();
        var tree = SeedTree(db);
        db.PhoneTreeEvents.Add(new PhoneTreeEvent
        {
            PhoneTreeId = tree.Id, StartedAt = DateTime.UtcNow.AddDays(-30), Status = "completed",
        });
        db.SaveChanges();

        var service = new DirectoryService(db, TestTenantScopes.Unrestricted);

        var act = () => service.DeletePhoneTreeAsync(tree.Id);

        // PhoneTree -> PhoneTreeEvent is a cascade, and each event cascades again to its
        // participants, dispatch steps and debrief log. Tidying up a code list would have
        // taken the lot.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*code call*");

        db.PhoneTreeEvents.Count().Should().Be(1, "the history must survive the attempt");
    }

    /// <summary>A code type nobody ever used carries no history, so it can still go.</summary>
    [Fact]
    public async Task AnUnusedCodeTypeCanStillBeDeleted()
    {
        using var db = CreateDb();
        var tree = SeedTree(db, "Typo Code");

        var service = new DirectoryService(db, TestTenantScopes.Unrestricted);
        await service.DeletePhoneTreeAsync(tree.Id);

        db.PhoneTrees.Any(t => t.Id == tree.Id).Should().BeFalse();
    }

    // ── There is no way to delete an incident at all ─────────────────────────────────────

    /// <summary>
    /// The service used to expose <c>DeleteEventAsync</c>, reached by an admin-only endpoint.
    /// The console's Delete button was removed; the route outlived it. Neither should exist,
    /// and this fails if either is reintroduced.
    /// </summary>
    [Fact]
    public void NothingOnTheServiceCanDeleteAnIncident()
    {
        var methods = typeof(IPhoneTreeEventService).GetMethods().Select(m => m.Name).ToList();

        methods.Should().NotContain("DeleteEventAsync");
        methods.Should().NotContain(n => n.Contains("Delete", StringComparison.Ordinal)
                                         && n.Contains("Event", StringComparison.Ordinal));
    }

    // ── Deleting a schedule must not take the on-call record ────────────────────────────

    /// <summary>
    /// Schedule → Shift is a cascade with no blocker check, unlike the employee path which
    /// refuses a hard delete outright. A worked shift is the record of who was on call, which
    /// is what an incident timeline is reconstructed from.
    /// </summary>
    [Fact]
    public async Task AScheduleWithWorkedShiftsCannotBeDeleted()
    {
        using var db = CreateDb();
        var schedule = new Schedule { Name = "Cardiology Nights", DepartmentId = 1, IsActive = true };
        db.Schedules.Add(schedule);
        db.SaveChanges();

        db.Shifts.Add(new Shift
        {
            ScheduleId = schedule.Id,
            StartTime = DateTime.UtcNow.AddDays(-2),
            EndTime = DateTime.UtcNow.AddDays(-2).AddHours(12),
        });
        db.SaveChanges();

        var service = NewScheduleService(db);

        var act = () => service.DeleteScheduleAsync(schedule.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*on call*");
        db.Shifts.Count().Should().Be(1);
    }

    /// <summary>A schedule whose shifts are all still ahead is a plan, not a record.</summary>
    [Fact]
    public async Task AScheduleOfFutureShiftsCanStillBeDeleted()
    {
        using var db = CreateDb();
        var schedule = new Schedule { Name = "Drafted By Mistake", DepartmentId = 1, IsActive = true };
        db.Schedules.Add(schedule);
        db.SaveChanges();

        db.Shifts.Add(new Shift
        {
            ScheduleId = schedule.Id,
            StartTime = DateTime.UtcNow.AddDays(7),
            EndTime = DateTime.UtcNow.AddDays(7).AddHours(12),
        });
        db.SaveChanges();

        await NewScheduleService(db).DeleteScheduleAsync(schedule.Id);

        db.Schedules.Any(s => s.Id == schedule.Id).Should().BeFalse();
    }

    // ── Who responded, on a closed incident, is part of the record ──────────────────────

    /// <summary>
    /// The last delete still reaching retained incident data. A participant row carries who was
    /// paged and when they acknowledged; removing one from a resolved code call edits the answer
    /// to "who responded, and how long did they take".
    /// </summary>
    [Fact]
    public async Task AParticipantCannotBeRemovedFromAResolvedIncident()
    {
        using var db = CreateDb();
        var tree = SeedTree(db);
        var evt = new PhoneTreeEvent
        {
            PhoneTreeId = tree.Id, StartedAt = DateTime.UtcNow.AddDays(-9),
            EndedAt = DateTime.UtcNow.AddDays(-9).AddMinutes(6), Status = "completed",
        };
        db.PhoneTreeEvents.Add(evt);
        db.SaveChanges();

        var participant = new PhoneTreeEventParticipant
        {
            PhoneTreeEventId = evt.Id, Role = "responder", AcknowledgedAt = DateTime.UtcNow.AddDays(-9),
        };
        db.PhoneTreeEventParticipants.Add(participant);
        db.SaveChanges();

        var service = NewEventService(db);

        var act = () => service.RemoveParticipantAsync(participant.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*retained record*");
        db.PhoneTreeEventParticipants.Count().Should().Be(1);
    }

    /// <summary>While the code is still running, correcting the roster is ordinary work.</summary>
    [Fact]
    public async Task AParticipantCanStillBeRemovedWhileTheIncidentIsActive()
    {
        using var db = CreateDb();
        var tree = SeedTree(db);
        var evt = new PhoneTreeEvent
        {
            PhoneTreeId = tree.Id, StartedAt = DateTime.UtcNow.AddMinutes(-4), Status = "active",
        };
        db.PhoneTreeEvents.Add(evt);
        db.SaveChanges();

        var participant = new PhoneTreeEventParticipant { PhoneTreeEventId = evt.Id, Role = "responder" };
        db.PhoneTreeEventParticipants.Add(participant);
        db.SaveChanges();

        await NewEventService(db).RemoveParticipantAsync(participant.Id);

        db.PhoneTreeEventParticipants.Should().BeEmpty();
    }

    // ── Retention that is actually enforced ──────────────────────────────────────────────

    private static IConfiguration Config(int? retentionDays) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            retentionDays is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { [RetentionPolicy.ConfigKey] = retentionDays.Value.ToString() })
            .Build();

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "OnCallApi";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void SevenYearsIsTheFloor()
    {
        RetentionPolicy.MinimumDays.Should().Be(2555);
    }

    /// <summary>
    /// The whole point. This setting sat in appsettings, Bicep and three documents for months
    /// while being read by nothing — so a value below policy was indistinguishable from one
    /// that met it, right up until a record was asked for.
    /// </summary>
    [Fact]
    public void AProductionInstanceRefusesToStartBelowTheFloor()
    {
        var act = () => RetentionPolicy.Validate(Config(2190), new Env(), NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>().WithMessage("*2555*");
    }

    [Fact]
    public void AtOrAboveTheFloorItStartsNormally()
    {
        var act = () => RetentionPolicy.Validate(Config(2555), new Env(), NullLogger.Instance);
        act.Should().NotThrow();

        var longer = () => RetentionPolicy.Validate(Config(3650), new Env(), NullLogger.Instance);
        longer.Should().NotThrow("keeping records longer than the floor is never the problem");
    }

    /// <summary>A laptop is not keeping anybody's records, so this warns rather than blocks.</summary>
    [Fact]
    public void DevelopmentOnlyWarns()
    {
        var act = () => RetentionPolicy.Validate(
            Config(30), new Env { EnvironmentName = "Development" }, NullLogger.Instance);

        act.Should().NotThrow();
    }

    /// <summary>An absent setting must not read as zero retention.</summary>
    [Fact]
    public void AnUnsetValueDefaultsToTheFloor()
    {
        RetentionPolicy.ConfiguredDays(Config(null)).Should().Be(RetentionPolicy.MinimumDays);
    }
}
