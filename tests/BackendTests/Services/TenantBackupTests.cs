using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// A subscription taking its own data out, and putting it back.
///
/// The database backups protect the deployment, not a customer: every tenant shares one
/// database, so a point-in-time restore rolls them all back together and can never answer "we
/// deleted our directory by mistake". That gap is widest for a subscription whose staff were
/// uploaded from a spreadsheet rather than synced from Entra — nothing upstream holds a second
/// copy, so this export is their whole disaster-recovery story.
///
/// Which makes two properties load-bearing, and both are pinned here: an export must contain
/// exactly one tenant's data and no one else's, and a restore must never be able to destroy
/// what survived or to invent history that never happened.
/// </summary>
public class TenantBackupTests
{
    private const int Mine = 7001;
    private const int Theirs = 7002;

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static TenantBackupService NewService(AppDbContext db) =>
        new(db, NullLogger<TenantBackupService>.Instance);

    /// <summary>Two subscriptions, each with a department, a person and a code tree.</summary>
    private static AppDbContext SeedTwoTenants()
    {
        var db = CreateDb();
        db.Tenants.AddRange(
            new Tenant { Id = Mine, Name = "Northwood", IsActive = true },
            new Tenant { Id = Theirs, Name = "Southvale", IsActive = true });

        db.Departments.AddRange(
            new Department { Id = 1, Name = "Cardiology", TenantId = Mine, IsActive = true },
            new Department { Id = 2, Name = "Oncology", TenantId = Theirs, IsActive = true });

        db.Employees.AddRange(
            new Employee
            {
                Id = Guid.NewGuid(), FirstName = "Mine", LastName = "Person",
                Email = "mine@northwood.test", TenantId = Mine, DepartmentId = 1,
                Source = "CsvImport", IsActive = true,
            },
            new Employee
            {
                Id = Guid.NewGuid(), FirstName = "Theirs", LastName = "Person",
                Email = "theirs@southvale.test", TenantId = Theirs, DepartmentId = 2,
                Source = "CsvImport", IsActive = true,
            });

        db.PhoneTrees.AddRange(
            new PhoneTree { Id = 1, Name = "Code Blue", TreeType = "code-blue", DepartmentId = 1, IsActive = true },
            new PhoneTree { Id = 2, Name = "Their Code", TreeType = "code-red", DepartmentId = 2, IsActive = true });

        db.SaveChanges();
        return db;
    }

    // ── Isolation: the property that must never break ───────────────────────────────────

    [Fact]
    public async Task AnExportContainsOnlyTheRequestedSubscription()
    {
        using var db = SeedTwoTenants();

        var backup = await NewService(db).ExportAsync(Mine);

        backup.TenantName.Should().Be("Northwood");
        backup.Departments.Should().ContainSingle().Which.Name.Should().Be("Cardiology");
        backup.Employees.Should().ContainSingle().Which.Email.Should().Be("mine@northwood.test");
        backup.PhoneTrees.Should().ContainSingle().Which.Name.Should().Be("Code Blue");

        // Said as a whole-archive assertion too: a future field that accidentally widened the
        // query would fail here even if the per-collection checks above were updated.
        var json = System.Text.Json.JsonSerializer.Serialize(backup);
        json.Should().NotContain("southvale", "one customer's archive must never carry another's data");
        json.Should().NotContain("Oncology");
    }

    [Fact]
    public async Task ShiftsAndPeopleAreReferencedByNameNotRowId()
    {
        using var db = SeedTwoTenants();
        var employee = db.Employees.Single(e => e.TenantId == Mine);

        var schedule = new Schedule { Name = "Nights", DepartmentId = 1, IsActive = true };
        db.Schedules.Add(schedule);
        db.SaveChanges();
        db.Shifts.Add(new Shift
        {
            ScheduleId = schedule.Id, EmployeeId = employee.Id,
            StartTime = DateTime.UtcNow.AddDays(-1), EndTime = DateTime.UtcNow.AddDays(-1).AddHours(8),
        });
        db.SaveChanges();

        var backup = await NewService(db).ExportAsync(Mine);

        // Ids mean nothing in a rebuilt database, which is exactly where a restore happens.
        backup.Schedules.Should().ContainSingle().Which.DepartmentName.Should().Be("Cardiology");
        var shift = backup.Shifts.Should().ContainSingle().Subject;
        shift.ScheduleName.Should().Be("Nights");
        shift.EmployeeEmail.Should().Be("mine@northwood.test");
    }

    // ── Secrets do not travel ───────────────────────────────────────────────────────────

    /// <summary>
    /// An archive is downloaded, copied to laptops and mailed around. A client secret inside
    /// one has escaped, and a masked placeholder restored later would look configured and
    /// silently not work — so the value is withheld and the omission is declared.
    /// </summary>
    [Fact]
    public async Task SensitiveSettingsAreWithheldRatherThanExported()
    {
        using var db = SeedTwoTenants();
        db.AppSettings.AddRange(
            new AppSetting { Key = "integrations.twilio.authToken", Value = "super-secret", TenantId = Mine },
            new AppSetting { Key = "scheduling.timeZone", Value = "America/New_York", TenantId = Mine });
        db.SaveChanges();

        var backup = await NewService(db).ExportAsync(Mine);

        var secret = backup.Settings.Single(s => s.Key == "integrations.twilio.authToken");
        secret.Value.Should().BeNull();
        secret.Withheld.Should().BeTrue();

        var ordinary = backup.Settings.Single(s => s.Key == "scheduling.timeZone");
        ordinary.Value.Should().Be("America/New_York");
        ordinary.Withheld.Should().BeFalse();

        System.Text.Json.JsonSerializer.Serialize(backup).Should().NotContain("super-secret");
    }

    // ── Restore: additive, repeatable, and unable to invent history ─────────────────────

    [Fact]
    public async Task RestoringIntoAFreshSubscriptionRebuildsTheConfiguration()
    {
        using var source = SeedTwoTenants();
        var backup = await NewService(source).ExportAsync(Mine);

        using var target = CreateDb();
        target.Tenants.Add(new Tenant { Id = 9001, Name = "Northwood (rebuilt)", IsActive = true });
        target.SaveChanges();

        var report = await NewService(target).RestoreAsync(9001, backup);

        report.DepartmentsCreated.Should().Be(1);
        report.EmployeesCreated.Should().Be(1);
        report.PhoneTreesCreated.Should().Be(1);

        target.Employees.Single(e => e.TenantId == 9001).Email.Should().Be("mine@northwood.test");
        target.Departments.Single(d => d.TenantId == 9001).Name.Should().Be("Cardiology");
    }

    /// <summary>
    /// Restore is reached for in a hurry, often more than once. Running it twice must be safe.
    /// </summary>
    [Fact]
    public async Task RestoringTwiceCreatesNothingTheSecondTime()
    {
        using var source = SeedTwoTenants();
        var backup = await NewService(source).ExportAsync(Mine);

        using var target = CreateDb();
        target.Tenants.Add(new Tenant { Id = 9001, Name = "Rebuilt", IsActive = true });
        target.SaveChanges();

        await NewService(target).RestoreAsync(9001, backup);
        var second = await NewService(target).RestoreAsync(9001, backup);

        second.EmployeesCreated.Should().Be(0);
        second.EmployeesSkipped.Should().Be(1);
        second.DepartmentsCreated.Should().Be(0);
        second.PhoneTreesCreated.Should().Be(0);

        target.Employees.Count(e => e.TenantId == 9001).Should().Be(1, "no duplicate person");
    }

    /// <summary>A restore must never be able to remove what survived the loss.</summary>
    [Fact]
    public async Task RestoringNeverDeletesWhatIsAlreadyThere()
    {
        using var source = SeedTwoTenants();
        var backup = await NewService(source).ExportAsync(Mine);

        using var target = CreateDb();
        target.Tenants.Add(new Tenant { Id = 9001, Name = "Rebuilt", IsActive = true });
        target.Departments.Add(new Department { Id = 50, Name = "Survived", TenantId = 9001, IsActive = true });
        target.Employees.Add(new Employee
        {
            Id = Guid.NewGuid(), FirstName = "Still", LastName = "Here",
            Email = "survivor@northwood.test", TenantId = 9001, IsActive = true,
        });
        target.SaveChanges();

        await NewService(target).RestoreAsync(9001, backup);

        target.Departments.Any(d => d.Name == "Survived").Should().BeTrue();
        target.Employees.Any(e => e.Email == "survivor@northwood.test").Should().BeTrue();
    }

    /// <summary>
    /// The archive carries incident history so the customer holds their own record — but a
    /// restore must not write it back. If it did, an emergency page, its timeline and its
    /// debrief could be authored by anyone holding a JSON file.
    /// </summary>
    [Fact]
    public async Task IncidentHistoryIsExportedButNeverWrittenBack()
    {
        using var source = SeedTwoTenants();
        source.PhoneTreeEvents.Add(new PhoneTreeEvent
        {
            PhoneTreeId = 1, StartedAt = DateTime.UtcNow.AddDays(-3), Status = "completed",
            InitiatedByName = "Divine Yisa", InitiatedByEmail = "divine@northwood.test",
            Location = "3 N",
        });
        source.SaveChanges();

        var backup = await NewService(source).ExportAsync(Mine);
        backup.Incidents.Should().ContainSingle()
            .Which.InitiatedByEmail.Should().Be("divine@northwood.test");

        using var target = CreateDb();
        target.Tenants.Add(new Tenant { Id = 9001, Name = "Rebuilt", IsActive = true });
        target.SaveChanges();

        var report = await NewService(target).RestoreAsync(9001, backup);

        target.PhoneTreeEvents.Should().BeEmpty("history cannot be restored from a file");
        report.IncidentsInArchive.Should().Be(1, "but the caller is told it was in the archive");
    }

    /// <summary>
    /// An archive from a newer version may contain data this build does not know how to write.
    /// Silently dropping it would be the worst outcome: the restore would report success.
    /// </summary>
    [Fact]
    public async Task AnArchiveFromANewerVersionIsRefused()
    {
        using var db = SeedTwoTenants();
        var backup = await NewService(db).ExportAsync(Mine);
        backup.FormatVersion = TenantBackupService.CurrentFormatVersion + 1;

        var act = () => NewService(db).RestoreAsync(Mine, backup);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*upgrade OnCall*");
    }

    [Fact]
    public async Task ARestoredPersonIsNotMarkedAsComingFromTheDirectory()
    {
        using var source = SeedTwoTenants();
        var backup = await NewService(source).ExportAsync(Mine);

        using var target = CreateDb();
        target.Tenants.Add(new Tenant { Id = 9001, Name = "Rebuilt", IsActive = true });
        target.SaveChanges();

        await NewService(target).RestoreAsync(9001, backup);

        // Source decides how a later directory sync treats the record. Claiming "Ad" for
        // somebody the directory has never seen would invite a sync to deactivate them.
        target.Employees.Single(e => e.TenantId == 9001).Source.Should().Be("CsvImport");
    }
}
