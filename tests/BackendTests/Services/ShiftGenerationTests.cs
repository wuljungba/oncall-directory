using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Generated rotations must cover the clock people actually work to.
///
/// Three defects compounded here. Shifts were built from DateTime.UtcNow.Date plus an hour
/// offset, so "7a-7p" meant 07:00 UTC — 2am Eastern. The night shift was created as
/// "secondary", leaving no primary on call between 19:00 and 07:00. And the rotation index
/// mixed a running count that advances twice a day with DayOfYear, so it skipped people.
///
/// The middle one is the dangerous one: code-call SMS resolution looks specifically for the
/// primary, so an overnight code call found nobody to text.
/// </summary>
public class ShiftGenerationTests
{
    private const string Eastern = "America/New_York";

    private static AppDbContext CreateDb(int employeeCount = 4)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);

        db.Departments.Add(new Department { Id = 1, Name = "Cardiology", IsActive = true });
        for (var i = 1; i <= employeeCount; i++)
        {
            db.Employees.Add(new Employee
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{i:D12}"),
                FirstName = $"Doc{i}", LastName = "Test", Email = $"doc{i}@test.example",
                AzureAdObjectId = $"oid-{i}", DepartmentId = 1, IsActive = true,
            });
        }
        db.Schedules.Add(new Schedule { Id = 1, Name = "Cardiology call", DepartmentId = 1 });
        db.SaveChanges();
        return db;
    }

    private static ScheduleService CreateService(AppDbContext db, string timeZone = Eastern) =>
        new(db, NullLogger<ScheduleService>.Instance, TestTenantScopes.Unrestricted, null,
            Options.Create(new SchedulingOptions { TimeZone = timeZone }));

    [Fact]
    public async Task EveryHourOfTheDayHasAPrimaryOnCall()
    {
        // The bug this guards: no primary existed between 19:00 and 07:00, so an overnight
        // code call resolved no mobile number to text.
        var db = CreateDb();
        var shifts = await CreateService(db).GenerateShiftsAsync(1, weeks: 1);

        shifts.Should().NotBeEmpty();
        shifts.Should().OnlyContain(s => s.Tier == "primary");

        // Walk the second generated day hour by hour; every hour must be covered.
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Eastern);
        var probeDay = TimeZoneInfo.ConvertTimeFromUtc(shifts.Min(s => s.StartTime), zone).Date.AddDays(1);

        for (var hour = 0; hour < 24; hour++)
        {
            var localInstant = probeDay.AddHours(hour).AddMinutes(30);
            var utcInstant = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localInstant, DateTimeKind.Unspecified), zone);

            shifts.Should().Contain(
                s => s.Tier == "primary" && s.StartTime <= utcInstant && s.EndTime > utcInstant,
                $"{hour:D2}:30 local must have a primary on call");
        }
    }

    [Fact]
    public async Task DayShiftStartsAtSevenLocalNotSevenUtc()
    {
        var db = CreateDb();
        var shifts = await CreateService(db).GenerateShiftsAsync(1, weeks: 1);

        var zone = TimeZoneInfo.FindSystemTimeZoneById(Eastern);
        var localStarts = shifts
            .Select(s => TimeZoneInfo.ConvertTimeFromUtc(s.StartTime, zone).Hour)
            .Distinct()
            .OrderBy(h => h);

        localStarts.Should().BeEquivalentTo([7, 19]);
    }

    [Fact]
    public async Task RotationVisitsEveryoneBeforeRepeating()
    {
        // The old index advanced twice per day and mixed in DayOfYear, so with an even
        // number of staff it could hand every shift to the same half of the team.
        var db = CreateDb(employeeCount: 4);
        var shifts = (await CreateService(db).GenerateShiftsAsync(1, weeks: 1))
            .OrderBy(s => s.StartTime)
            .ToList();

        shifts.Take(4).Select(s => s.EmployeeId).Distinct().Should().HaveCount(4);
    }

    [Fact]
    public async Task ConsecutiveShiftsGoToDifferentPeople()
    {
        var db = CreateDb(employeeCount: 3);
        var shifts = (await CreateService(db).GenerateShiftsAsync(1, weeks: 1))
            .OrderBy(s => s.StartTime)
            .ToList();

        for (var i = 1; i < shifts.Count; i++)
        {
            shifts[i].EmployeeId.Should().NotBe(shifts[i - 1].EmployeeId,
                "nobody should be handed back-to-back 12-hour shifts by the generator");
        }
    }

    [Fact]
    public async Task ShiftsAreContiguousWithNoGaps()
    {
        var db = CreateDb();
        var shifts = (await CreateService(db).GenerateShiftsAsync(1, weeks: 1))
            .OrderBy(s => s.StartTime)
            .ToList();

        for (var i = 1; i < shifts.Count; i++)
        {
            shifts[i].StartTime.Should().Be(shifts[i - 1].EndTime,
                "coverage must not have a hole between shifts");
        }
    }

    [Fact]
    public async Task UnknownTimeZone_FallsBackToUtcRatherThanFailing()
    {
        // A bad configuration value must not stop a schedule being generated — it is
        // logged at Error instead.
        var db = CreateDb();

        var shifts = await CreateService(db, timeZone: "Not/AZone").GenerateShiftsAsync(1, weeks: 1);

        shifts.Should().NotBeEmpty();
        shifts.Select(s => s.StartTime.Hour).Distinct().OrderBy(h => h)
            .Should().BeEquivalentTo([7, 19], "with UTC as the fallback the hours are UTC hours");
    }

    [Fact]
    public async Task RegeneratingDoesNotDuplicateExistingDays()
    {
        var db = CreateDb();
        var service = CreateService(db);

        var first = await service.GenerateShiftsAsync(1, weeks: 1);
        var second = await service.GenerateShiftsAsync(1, weeks: 1);

        first.Should().NotBeEmpty();
        second.Should().BeEmpty("those local dates already have shifts");
    }
}
