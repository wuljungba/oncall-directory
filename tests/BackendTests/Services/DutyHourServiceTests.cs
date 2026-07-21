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

        var service = new DutyHourService(db, NullLogger<DutyHourService>.Instance);
        var result = await service.CheckComplianceAsync(employeeId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAllComplianceAsync_AllClear_ReturnsEmpty()
    {
        var db = CreateDbContext();

        var service = new DutyHourService(db, NullLogger<DutyHourService>.Instance);
        var result = await service.CheckAllComplianceAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRulesAsync_ReturnsEnabledRules()
    {
        var db = CreateDbContext();

        var service = new DutyHourService(db, NullLogger<DutyHourService>.Instance);
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

        var service = new DutyHourService(db, NullLogger<DutyHourService>.Instance);
        var result = await service.GetHoursWorkedAsync(employeeId, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        result.Should().Be(0);
    }
}
