using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OnCallApi.Authorization;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;
using System.Security.Claims;
using ValidationException = System.ComponentModel.DataAnnotations.ValidationException;

namespace BackendTests.Services;

/// <summary>
/// A foreign key that does not resolve must be a plain validation error, not a 500.
///
/// Nothing checked these, so a departmentId or managerId that did not exist travelled all
/// the way to SaveChanges and came back as "SQLite Error 19: FOREIGN KEY constraint failed"
/// wrapped in an opaque "An internal error occurred" -- naming neither the field nor the
/// value. The bulk importer had validated department ids all along; the single-record admin
/// path had simply been missed.
///
/// The two exception types carry different meanings, and the controller maps them
/// differently: ValidationException means bad input (400), InvalidOperationException means a
/// conflict with what already exists, such as a duplicate email (409).
/// </summary>
public class AdminServiceReferenceValidationTests
{
    private const int RealDepartment = 1;
    private const int MissingDepartment = 99999;
    private static readonly Guid RealManager = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid MissingManager = Guid.Parse("ffffffff-0000-0000-0000-00000000ffff");

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);

        db.Tenants.Add(new Tenant { Id = 1, Name = "Main Hospital", IsActive = true });
        db.Departments.Add(new Department { Id = RealDepartment, Name = "Emergency Medicine", TenantId = 1, IsActive = true });
        db.Employees.Add(new Employee
        {
            Id = RealManager,
            AzureAdObjectId = "oid-manager",
            FirstName = "Ada",
            LastName = "Manager",
            Email = "ada.manager@example.test",
            DepartmentId = RealDepartment,
            TenantId = 1,
            IsActive = true,
        });
        db.SaveChanges();
        return db;
    }

    /// <summary>
    /// A super admin, so tenant scoping never masks the behaviour under test.
    ///
    /// This matters for the update path: ownership is checked BEFORE references are
    /// validated, which is the right order -- you should not learn whether a foreign key
    /// resolves on a record you are not allowed to see. Tenant scoping itself is covered by
    /// AdminServiceTenantScopingTests.
    /// </summary>
    private static AdminService CreateService(AppDbContext db)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(Permissions.ClaimType, Permissions.AdminFull)], "Test"));

        var tenantContext = new Mock<ITenantContextService>();
        tenantContext.Setup(t => t.IsSuperAdmin(It.IsAny<ClaimsPrincipal>())).Returns(true);

        var httpContextMock = new Mock<IHttpContextAccessor>();
        httpContextMock.Setup(x => x.HttpContext).Returns(new DefaultHttpContext { User = user });

        return new AdminService(
            db,
            NullLogger<AdminService>.Instance,
            tenantContext.Object,
            httpContextMock.Object,
            new Mock<IAuditService>().Object);
    }

    private static CreateEmployeeRequest NewEmployee(int? departmentId = null, Guid? managerId = null) =>
        new(
            AzureAdObjectId: null,
            FirstName: "Test",
            LastName: "Person",
            Email: $"test.person.{Guid.NewGuid():N}@example.test",
            Title: null,
            Specialty: null,
            ClinicalRole: null,
            OfficePhone: null,
            MobilePhone: null,
            PagerNumber: null,
            OfficeLocation: null,
            DepartmentId: departmentId,
            ManagerId: managerId,
            Certifications: null,
            Languages: null);

    [Fact]
    public async Task CreatingWithAMissingDepartmentIsAValidationError()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        var act = () => service.CreateEmployeeAsync(NewEmployee(departmentId: MissingDepartment));

        (await act.Should().ThrowAsync<ValidationException>())
            .WithMessage($"*{MissingDepartment}*", "the message must name the offending id");
        db.Employees.Count().Should().Be(1, "a rejected create must not persist anything");
    }

    [Fact]
    public async Task CreatingWithAMissingManagerIsAValidationError()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        var act = () => service.CreateEmployeeAsync(NewEmployee(managerId: MissingManager));

        await act.Should().ThrowAsync<ValidationException>();
        db.Employees.Count().Should().Be(1);
    }

    [Fact]
    public async Task CreatingWithValidReferencesSucceeds()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        var created = await service.CreateEmployeeAsync(
            NewEmployee(departmentId: RealDepartment, managerId: RealManager));

        created.DepartmentId.Should().Be(RealDepartment);
        created.ManagerId.Should().Be(RealManager);
    }

    [Fact]
    public async Task CreatingWithNoReferencesSucceeds()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        var created = await service.CreateEmployeeAsync(NewEmployee());

        created.Should().NotBeNull();
        created.DepartmentId.Should().BeNull();
    }

    /// <summary>A duplicate email is a conflict with existing state, not malformed input.</summary>
    [Fact]
    public async Task DuplicateEmailRemainsAConflictNotAValidationError()
    {
        using var db = CreateDb();
        var service = CreateService(db);
        var request = NewEmployee();
        await service.CreateEmployeeAsync(request);

        var act = () => service.CreateEmployeeAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdatingWithAMissingDepartmentIsAValidationError()
    {
        using var db = CreateDb();
        var service = CreateService(db);
        var created = await service.CreateEmployeeAsync(NewEmployee());

        var act = () => service.UpdateEmployeeAsync(created.Id, new UpdateEmployeeRequest(
            FirstName: "Test",
            LastName: "Person",
            Email: created.Email,
            Title: null,
            Specialty: null,
            ClinicalRole: null,
            OfficePhone: null,
            MobilePhone: null,
            PagerNumber: null,
            OfficeLocation: null,
            DepartmentId: MissingDepartment,
            ManagerId: null,
            Certifications: null,
            Languages: null,
            IsActive: null));

        await act.Should().ThrowAsync<ValidationException>();

        var untouched = await db.Employees.AsNoTracking().FirstAsync(e => e.Id == created.Id);
        untouched.DepartmentId.Should().BeNull("a rejected update must change nothing");
    }
}
