using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

public class DirectoryServiceTests
{
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        db.Departments.Add(new Department { Id = 1, Name = "Cardiology" });
        db.Departments.Add(new Department { Id = 2, Name = "ER" });
        db.Employees.Add(new Employee
        {
            Id = Guid.NewGuid(),
            AzureAdObjectId = "user-1",
            FirstName = "Jane",
            LastName = "Smith",
            Title = "MD",
            Email = "jane@test.com",
            DepartmentId = 1,
            IsActive = true,
        });
        db.Employees.Add(new Employee
        {
            Id = Guid.NewGuid(),
            AzureAdObjectId = "user-2",
            FirstName = "John",
            LastName = "Doe",
            Title = "RN",
            Email = "john@test.com",
            DepartmentId = 2,
            IsActive = true,
        });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task SearchEmployeesAsync_ByFirstName_ReturnsMatches()
    {
        var db = CreateDbContext();
        var service = new DirectoryService(db);

        var result = await service.SearchEmployeesAsync("Jane");

        result.Should().HaveCount(1);
        result[0].FirstName.Should().Be("Jane");
    }

    [Fact]
    public async Task SearchEmployeesAsync_ByLastName_ReturnsMatches()
    {
        var db = CreateDbContext();
        var service = new DirectoryService(db);

        var result = await service.SearchEmployeesAsync("Doe");

        result.Should().HaveCount(1);
        result[0].LastName.Should().Be("Doe");
    }

    [Fact]
    public async Task SearchEmployeesAsync_ByEmail_ReturnsMatches()
    {
        var db = CreateDbContext();
        var service = new DirectoryService(db);

        var result = await service.SearchEmployeesAsync("jane@test.com");

        result.Should().HaveCount(1);
        result[0].Email.Should().Be("jane@test.com");
    }

    [Fact]
    public async Task SearchEmployeesAsync_EmptyQuery_ReturnsAllActive()
    {
        var db = CreateDbContext();
        var service = new DirectoryService(db);

        var result = await service.SearchEmployeesAsync("");

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetEmployeeByEmailAsync_ExistingEmail_ReturnsEmployee()
    {
        var db = CreateDbContext();
        var service = new DirectoryService(db);

        var result = await service.GetEmployeeByEmailAsync("jane@test.com");

        result.Should().NotBeNull();
        result!.FirstName.Should().Be("Jane");
    }

    [Fact]
    public async Task GetEmployeeByEmailAsync_MissingEmail_ReturnsNull()
    {
        var db = CreateDbContext();
        var service = new DirectoryService(db);

        var result = await service.GetEmployeeByEmailAsync("nobody@test.com");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetDepartmentEmployeesAsync_ReturnsCorrectDepartment()
    {
        var db = CreateDbContext();
        var service = new DirectoryService(db);

        var result = await service.GetDepartmentEmployeesAsync(1);

        result.Should().HaveCount(1);
        result[0].DepartmentId.Should().Be(1);
    }
}
