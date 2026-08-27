using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

public class DutyHourServiceTests
{
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        db.Departments.Add(new Department { Id = 1, Name = "Test Dept" });
        db.DutyHourRules.Add(new DutyHourRule
        {
            Id = 1,
            Name = "80-hour weekly limit",
            MaxHoursPerPeriod = 80,
            PeriodDays = 7,
            MinHoursBetweenShifts = 10,
            MaxShiftLengthHours = 24,
            MaxConsecutiveDays = 7,
            IsEnabled = true,
        });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task CheckComplianceAsync_NoShifts_NoViolations()
    {
        var db = CreateDbContext();
        var employeeId = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            Id = employeeId,
            AzureAdObjectId = "test-obj",
            FirstName = "Test",
            LastName = "User",
            Email = "test@test.com",
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new DutyHourService(db, TestTenantScopes.Unrestricted, NullLogger<DutyHourService>.Instance);
        var result = await service.CheckComplianceAsync(employeeId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAllComplianceAsync_AllClear_ReturnsEmpty()
    {
        var db = CreateDbContext();

        var service = new DutyHourService(db, TestTenantScopes.Unrestricted, NullLogger<DutyHourService>.Instance);
        var result = await service.CheckAllComplianceAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRulesAsync_ReturnsEnabledRules()
    {
        var db = CreateDbContext();

        var service = new DutyHourService(db, TestTenantScopes.Unrestricted, NullLogger<DutyHourService>.Instance);
        var result = await service.GetRulesAsync();

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("80-hour weekly limit");
    }

    [Fact]
    public async Task GetHoursWorkedAsync_NoShifts_ReturnsZero()
    {
        var db = CreateDbContext();
        var employeeId = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            Id = employeeId,
            AzureAdObjectId = "test-obj",
            FirstName = "Test",
            LastName = "User",
            Email = "test@test.com",
        });
        await db.SaveChangesAsync();

        var service = new DutyHourService(db, TestTenantScopes.Unrestricted, NullLogger<DutyHourService>.Instance);
        var result = await service.GetHoursWorkedAsync(employeeId, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        result.Should().Be(0);
    }

    [Fact]
    public async Task CheckComplianceAsync_ExceedsWeeklyLimit_PersistsBreachWithNavs()
    {
        var db = CreateDbContext();
        var employeeId = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            Id = employeeId,
            AzureAdObjectId = "test-obj",
            FirstName = "Test",
            LastName = "User",
            Email = "test@test.com",
        });

        // 7 x 12h shifts (84h) across the last 7 full days = breach of the 80h/7d rule;
        // the 12h gaps satisfy the ≥10h rest rule and 7 days does not exceed 7 consecutive.
        for (var i = 7; i >= 1; i--)
        {
            var day = DateTime.UtcNow.Date.AddDays(-i);
            db.Shifts.Add(new Shift
            {
                EmployeeId = employeeId,
                StartTime = day.AddHours(12),
                EndTime = day.AddHours(24),
                Tier = "primary",
                Status = "scheduled",
            });
        }
        await db.SaveChangesAsync();

        var service = new DutyHourService(db, TestTenantScopes.Unrestricted, NullLogger<DutyHourService>.Instance);
        var result = await service.CheckComplianceAsync(employeeId);

        result.Should().ContainSingle(v => v.Description.Contains("80"));
        result[0].Severity.Should().Be(2);
        result[0].Employee.Should().NotBeNull();
        result[0].Employee!.FirstName.Should().Be("Test");
        result[0].Rule.Should().NotBeNull();
        result[0].Rule!.Name.Should().Be("80-hour weekly limit");

        // Persisted, not just returned.
        db.DutyHourViolations.Count().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CheckComplianceAsync_IgnoresSwappedAndGapShifts()
    {
        var db = CreateDbContext();
        var employeeId = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            Id = employeeId,
            AzureAdObjectId = "test-obj",
            FirstName = "Test",
            LastName = "User",
            Email = "test@test.com",
        });

        var day = DateTime.UtcNow.Date;
        db.Shifts.Add(new Shift { EmployeeId = employeeId, StartTime = day.AddHours(12), EndTime = day.AddHours(24), Status = "scheduled" });
        db.Shifts.Add(new Shift { EmployeeId = employeeId, StartTime = day.AddHours(12), EndTime = day.AddHours(24), Status = "gap" });
        db.Shifts.Add(new Shift { EmployeeId = employeeId, StartTime = day.AddHours(12), EndTime = day.AddHours(24), Status = "swapped" });
        await db.SaveChangesAsync();

        var service = new DutyHourService(db, TestTenantScopes.Unrestricted, NullLogger<DutyHourService>.Instance);
        var result = await service.GetHoursWorkedAsync(employeeId, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

        // Only the "scheduled" shift counts (12h); the gap/swapped ones are rest.
        result.Should().Be(12);
    }
}
