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

namespace BackendTests.Services;

public class AdminServiceTenantScopingTests
{
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        SeedData(db);
        return db;
    }

    private static void SeedData(AppDbContext db)
    {
        // Tenant 1 — Main Hospital
        db.Tenants.Add(new Tenant { Id = 1, Name = "Main Hospital", IsActive = true, CreatedAt = DateTime.UtcNow });
        db.Tenants.Add(new Tenant { Id = 2, Name = "North Campus", IsActive = true, CreatedAt = DateTime.UtcNow });

        // Departments — Tenant 1
        db.Departments.Add(new Department { Id = 1, Name = "Emergency Medicine", TenantId = 1, IsActive = true });
        db.Departments.Add(new Department { Id = 2, Name = "Cardiology", TenantId = 1, IsActive = true });

        // Departments — Tenant 2
        db.Departments.Add(new Department { Id = 3, Name = "Radiology", TenantId = 2, IsActive = true });

        // Employees — Tenant 1
        db.Employees.Add(new Employee
        {
            Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            AzureAdObjectId = "tenant1-user-a",
            FirstName = "Alice",
            LastName = "Adams",
            Email = "alice@main.com",
            DepartmentId = 1,
            TenantId = 1,
            IsActive = true,
        });
        db.Employees.Add(new Employee
        {
            Id = Guid.Parse("10000000-0000-0000-0000-000000000002"),
            AzureAdObjectId = "tenant1-user-b",
            FirstName = "Bob",
            LastName = "Baker",
            Email = "bob@main.com",
            DepartmentId = 2,
            TenantId = 1,
            IsActive = true,
        });

        // Employees — Tenant 2
        db.Employees.Add(new Employee
        {
            Id = Guid.Parse("20000000-0000-0000-0000-000000000001"),
            AzureAdObjectId = "tenant2-user-c",
            FirstName = "Carol",
            LastName = "Clark",
            Email = "carol@north.com",
            DepartmentId = 3,
            TenantId = 2,
            IsActive = true,
        });

        // Employees — No tenant (inactive)
        db.Employees.Add(new Employee
        {
            Id = Guid.Parse("30000000-0000-0000-0000-000000000001"),
            AzureAdObjectId = "no-tenant-user",
            FirstName = "Dave",
            LastName = "Davis",
            Email = "dave@legacy.com",
            IsActive = true,
        });

        // TenantAdmin assignments
        db.TenantAdmins.Add(new TenantAdmin
        {
            Id = 1,
            TenantId = 1,
            AzureAdObjectId = "admin-tenant1",
            Role = "DepartmentAdmin",
            IsAutoAssigned = false,
            CreatedAt = DateTime.UtcNow,
        });
        db.TenantAdmins.Add(new TenantAdmin
        {
            Id = 2,
            TenantId = 2,
            AzureAdObjectId = "admin-tenant2",
            Role = "DepartmentAdmin",
            IsAutoAssigned = false,
            CreatedAt = DateTime.UtcNow,
        });

        db.SaveChanges();
    }

    private static AdminService CreateAdminService(AppDbContext db, ITenantContextService tenantContext, ClaimsPrincipal? user = null)
    {
        var httpContextMock = new Mock<IHttpContextAccessor>();
        if (user != null)
        {
            var httpContext = new DefaultHttpContext { User = user };
            httpContextMock.Setup(x => x.HttpContext).Returns(httpContext);
        }

        return new AdminService(
            db,
            NullLogger<AdminService>.Instance,
            tenantContext,
            httpContextMock.Object,
            new Mock<IAuditService>().Object);
    }

    private static ClaimsPrincipal CreateSuperAdminPrincipal()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "superadmin-oid"),
            new(Permissions.ClaimType, Permissions.AdminFull),
            new(Permissions.ClaimType, Permissions.TenantManage),
        };
        var identity = new ClaimsIdentity(claims, "test-auth");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreateSubAdminPrincipal(string tenantAdminOid)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, tenantAdminOid),
            new("oid", tenantAdminOid),
            // Tenant claims will be resolved by TenantClaimsMiddleware at runtime,
            // but for service-level tests we rely on ITenantContextService
        };
        var identity = new ClaimsIdentity(claims, "test-auth");
        return new ClaimsPrincipal(identity);
    }

    // ── TenantContextService Tests ──

    [Fact]
    public async Task TenantContextService_SuperAdmin_SeesAllTenants()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var service = new TenantContextService(db, httpContextAccessor.Object);
        var user = CreateSuperAdminPrincipal();

        var tenantIds = await service.GetAuthorizedTenantIdsAsync(user);

        tenantIds.Should().HaveCount(2);
        tenantIds.Should().Contain([1, 2]);
    }

    [Fact]
    public async Task TenantContextService_SuperAdmin_IsSuperAdminReturnsTrue()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var service = new TenantContextService(db, httpContextAccessor.Object);
        var user = CreateSuperAdminPrincipal();

        service.IsSuperAdmin(user).Should().BeTrue();
    }

    [Fact]
    public async Task TenantContextService_SubAdmin_SeesOnlyAssignedTenant()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var service = new TenantContextService(db, httpContextAccessor.Object);
        var user = CreateSubAdminPrincipal("admin-tenant1");

        var tenantIds = await service.GetAuthorizedTenantIdsAsync(user);

        tenantIds.Should().HaveCount(1);
        tenantIds.Should().Contain(1);
        tenantIds.Should().NotContain(2);
    }

    [Fact]
    public async Task TenantContextService_SubAdmin_IsTenantAdminReturnsTrue()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var service = new TenantContextService(db, httpContextAccessor.Object);
        var user = CreateSubAdminPrincipal("admin-tenant1");

        var isAdmin = await service.IsTenantAdminAsync(user);

        isAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task TenantContextService_UnauthorizedUser_SeesNothing()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var service = new TenantContextService(db, httpContextAccessor.Object);
        var user = CreateSubAdminPrincipal("unknown-user");

        var tenantIds = await service.GetAuthorizedTenantIdsAsync(user);

        tenantIds.Should().BeEmpty();
    }

    [Fact]
    public async Task TenantContextService_NotSuperAdmin_IsSuperAdminReturnsFalse()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var service = new TenantContextService(db, httpContextAccessor.Object);
        var user = CreateSubAdminPrincipal("admin-tenant1");

        service.IsSuperAdmin(user).Should().BeFalse();
    }

    // ── AdminService Tenant Scoping Tests ──

    [Fact]
    public async Task GetAllEmployeesAsync_SuperAdmin_SeesAllEmployees()
    {
        var db = CreateDbContext();
        var tenantContext = new TenantContextService(db, new Mock<IHttpContextAccessor>().Object);
        var adminService = CreateAdminService(db, tenantContext, CreateSuperAdminPrincipal());

        var employees = await adminService.GetAllEmployeesAsync(true);

        employees.Should().HaveCount(4); // All employees including no-tenant
    }

    [Fact]
    public async Task GetAllEmployeesAsync_SubAdmin_SeesOnlyTenantEmployees()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext?)null);
        var tenantContext = new TenantContextService(db, httpContextAccessor.Object);
        var adminService = CreateAdminService(db, tenantContext, CreateSubAdminPrincipal("admin-tenant1"));

        var employees = await adminService.GetAllEmployeesAsync(true);

        employees.Should().HaveCount(2); // Alice and Bob from Tenant 1
        employees.Should().OnlyContain(e => e.TenantId == 1);
        employees.Select(e => e.FirstName).Should().NotContain("Carol"); // Tenant 2 employee
    }

    [Fact]
    public async Task GetAllEmployeesAsync_Tenant2SubAdmin_SeesOnlyTenant2Employees()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext?)null);
        var tenantContext = new TenantContextService(db, httpContextAccessor.Object);
        var adminService = CreateAdminService(db, tenantContext, CreateSubAdminPrincipal("admin-tenant2"));

        var employees = await adminService.GetAllEmployeesAsync(true);

        employees.Should().HaveCount(1);
        employees.Should().OnlyContain(e => e.TenantId == 2);
        employees[0].FirstName.Should().Be("Carol");
    }

    [Fact]
    public async Task GetAllEmployeesAsync_Unauthorized_SeesNothing()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext?)null);
        var tenantContext = new TenantContextService(db, httpContextAccessor.Object);
        var adminService = CreateAdminService(db, tenantContext, CreateSubAdminPrincipal("unknown-user"));

        var employees = await adminService.GetAllEmployeesAsync(true);

        employees.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllDepartmentsAsync_SuperAdmin_SeesAllDepartments()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext?)null);
        var tenantContext = new TenantContextService(db, httpContextAccessor.Object);
        var adminService = CreateAdminService(db, tenantContext, CreateSuperAdminPrincipal());

        var departments = await adminService.GetAllDepartmentsAsync(true);

        departments.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetAllDepartmentsAsync_SubAdmin_SeesOnlyTenantDepartments()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext?)null);
        var tenantContext = new TenantContextService(db, httpContextAccessor.Object);
        var adminService = CreateAdminService(db, tenantContext, CreateSubAdminPrincipal("admin-tenant1"));

        var departments = await adminService.GetAllDepartmentsAsync(true);

        departments.Should().HaveCount(2);
        departments.Should().OnlyContain(d => d.TenantId == 1);
        departments.Select(d => d.Name).Should().NotContain("Radiology");
    }

    [Fact]
    public async Task CreateEmployeeAsync_SubAdmin_AutoAssignsTenantId()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext?)null);
        var tenantContext = new TenantContextService(db, httpContextAccessor.Object);
        var adminService = CreateAdminService(db, tenantContext, CreateSubAdminPrincipal("admin-tenant1"));

        var request = new CreateEmployeeRequest(
            AzureAdObjectId: "new-user",
            FirstName: "Eve",
            LastName: "Edwards",
            Email: "eve@main.com",
            Title: "RN",
            Specialty: null,
            ClinicalRole: null,
            OfficePhone: null,
            MobilePhone: null,
            PagerNumber: null,
            OfficeLocation: null,
            DepartmentId: 1,
            ManagerId: null,
            Certifications: null,
            Languages: null);

        var employee = await adminService.CreateEmployeeAsync(request);

        employee.TenantId.Should().Be(1); // Auto-assigned to Tenant 1
    }

    [Fact]
    public async Task CreateEmployeeAsync_SuperAdmin_DoesNotAutoAssignTenantId()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext?)null);
        var tenantContext = new TenantContextService(db, httpContextAccessor.Object);
        var adminService = CreateAdminService(db, tenantContext, CreateSuperAdminPrincipal());

        var request = new CreateEmployeeRequest(
            AzureAdObjectId: "super-new-user",
            FirstName: "Frank",
            LastName: "Fisher",
            Email: "frank@system.com",
            Title: "Admin",
            Specialty: null,
            ClinicalRole: null,
            OfficePhone: null,
            MobilePhone: null,
            PagerNumber: null,
            OfficeLocation: null,
            DepartmentId: null,
            ManagerId: null,
            Certifications: null,
            Languages: null);

        var employee = await adminService.CreateEmployeeAsync(request);

        employee.TenantId.Should().BeNull(); // Super admin, no auto-assignment
    }

    [Fact]
    public async Task CreateDepartmentAsync_SubAdmin_AutoAssignsTenantId()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext?)null);
        var tenantContext = new TenantContextService(db, httpContextAccessor.Object);
        var adminService = CreateAdminService(db, tenantContext, CreateSubAdminPrincipal("admin-tenant1"));

        var request = new CreateDepartmentRequest("Neurology", "Brain & Spine", "Healthcare", null);

        var department = await adminService.CreateDepartmentAsync(request);

        department.TenantId.Should().Be(1);
    }

    [Fact]
    public async Task GetDepartmentMembersAsync_SubAdmin_SeesOnlyTenantMembers()
    {
        var db = CreateDbContext();
        // Add an employee from Tenant 2 to Department 2 to test cross-tenant isolation
        db.Employees.Add(new Employee
        {
            Id = Guid.Parse("20000000-0000-0000-0000-000000000002"),
            AzureAdObjectId = "cross-tenant-user",
            FirstName = "Grace",
            LastName = "Green",
            Email = "grace@north.com",
            DepartmentId = 2, // Same department ID but different tenant
            TenantId = 2,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext?)null);
        var tenantContext = new TenantContextService(db, httpContextAccessor.Object);
        var adminService = CreateAdminService(db, tenantContext, CreateSubAdminPrincipal("admin-tenant1"));

        // Department 2 is in Tenant 1, so sub-admin of Tenant 1 should see only Tenant 1 members
        var members = await adminService.GetDepartmentMembersAsync(2);

        members.Should().HaveCount(1);
        members[0].FirstName.Should().Be("Bob");
        members[0].FirstName.Should().NotBe("Grace");
    }

    [Fact]
    public async Task UpdateEmployeeAsync_SubAdmin_CanUpdateOwnTenantEmployee()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext?)null);
        var tenantContext = new TenantContextService(db, httpContextAccessor.Object);
        var adminService = CreateAdminService(db, tenantContext, CreateSubAdminPrincipal("admin-tenant1"));

        var request = new UpdateEmployeeRequest(
            FirstName: "Alice",
            LastName: "Adams-Updated",
            Email: "alice@main.com",
            Title: "Senior MD",
            Specialty: null,
            ClinicalRole: null,
            OfficePhone: null,
            MobilePhone: null,
            PagerNumber: null,
            OfficeLocation: null,
            DepartmentId: 1,
            ManagerId: null,
            Certifications: null,
            Languages: null,
            IsActive: true);

        var updated = await adminService.UpdateEmployeeAsync(
            Guid.Parse("10000000-0000-0000-0000-000000000001"), request);

        updated.LastName.Should().Be("Adams-Updated");
        updated.Title.Should().Be("Senior MD");
    }

    [Fact]
    public async Task UpdateEmployeeAsync_SubAdmin_CannotUpdateOtherTenantEmployee()
    {
        var db = CreateDbContext();
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(x => x.HttpContext).Returns((HttpContext?)null);
        var tenantContext = new TenantContextService(db, httpContextAccessor.Object);
        var adminService = CreateAdminService(db, tenantContext, CreateSubAdminPrincipal("admin-tenant1"));

        var request = new UpdateEmployeeRequest(
            FirstName: "Carol",
            LastName: "Clark-Hacked",
            Email: "carol@north.com",
            Title: null,
            Specialty: null,
            ClinicalRole: null,
            OfficePhone: null,
            MobilePhone: null,
            PagerNumber: null,
            OfficeLocation: null,
            DepartmentId: 3,
            ManagerId: null,
            Certifications: null,
            Languages: null,
            IsActive: null);

        // Attempting to update an employee from Tenant 2 should throw
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            adminService.UpdateEmployeeAsync(
                Guid.Parse("20000000-0000-0000-0000-000000000001"), request));
    }
}
