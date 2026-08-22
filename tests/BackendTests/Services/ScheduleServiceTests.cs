using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

public class ScheduleServiceTests
{
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        db.Departments.Add(new Department { Id = 1, Name = "Test Department" });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task GetSchedulesAsync_WhenNoSchedules_ReturnsEmpty()
    {
        var db = CreateDbContext();
        var service = new ScheduleService(db, NullLogger<ScheduleService>.Instance, TestTenantScopes.Unrestricted);

        var result = await service.GetSchedulesAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSchedulesAsync_WithSchedules_ReturnsAll()
    {
        var db = CreateDbContext();
        db.Schedules.Add(new Schedule
        {
            Name = "Test Schedule",
            DepartmentId = 1,
            RotationType = "weekly",
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddMonths(1),
        });
        await db.SaveChangesAsync();

        var service = new ScheduleService(db, NullLogger<ScheduleService>.Instance, TestTenantScopes.Unrestricted);
        var result = await service.GetSchedulesAsync();

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Test Schedule");
    }

    [Fact]
    public async Task CreateScheduleAsync_PersistsSchedule()
    {
        var db = CreateDbContext();
        var service = new ScheduleService(db, NullLogger<ScheduleService>.Instance, TestTenantScopes.Unrestricted);

        var schedule = new Schedule
        {
            Name = "New Schedule",
            DepartmentId = 1,
            RotationType = "monthly",
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddMonths(3),
        };

        var created = await service.CreateScheduleAsync(schedule);

        created.Id.Should().BeGreaterThan(0);
        created.Name.Should().Be("New Schedule");

        var fetched = await db.Schedules.FindAsync(created.Id);
        fetched.Should().NotBeNull();
    }

    [Fact]
    public async Task GetCurrentOnCallAsync_WhenNoActiveShifts_ReturnsEmpty()
    {
        var db = CreateDbContext();
        var service = new ScheduleService(db, NullLogger<ScheduleService>.Instance, TestTenantScopes.Unrestricted);

        var result = await service.GetCurrentOnCallAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetShiftsAsync_FiltersByDateRange()
    {
        var db = CreateDbContext();
        db.Schedules.Add(new Schedule
        {
            Id = 1,
            Name = "Test",
            DepartmentId = 1,
            RotationType = "weekly",
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddMonths(1),
        });

        db.Employees.Add(new Employee
        {
            Id = Guid.NewGuid(),
            AzureAdObjectId = "test-obj-id",
            FirstName = "Test",
            LastName = "User",
            Email = "test@test.com",
        });

        await db.SaveChangesAsync();

        var service = new ScheduleService(db, NullLogger<ScheduleService>.Instance, TestTenantScopes.Unrestricted);
        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow.AddDays(1);

        var result = await service.GetShiftsAsync(1, from, to);

        result.Should().BeEmpty(); // No shifts yet
    }
}
