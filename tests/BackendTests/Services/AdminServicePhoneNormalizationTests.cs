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
/// Phone numbers entered through the admin API must be stored canonically.
///
/// Employee.OfficePhone/MobilePhone/PagerNumber each carry a [RegularExpression] demanding
/// E.164, but nothing enforced it on this path, so whatever was typed was stored raw --
/// including values the dispatch path could never dial. An EmployeeValidator covering this
/// exists and is registered in DI, but nothing invokes it and it targets the entity rather
/// than the request record, so it was dead code.
///
/// The importer has normalized numbers all along; this brings the single-record path level
/// with it, so the same number entered either way ends up stored the same.
/// </summary>
public class AdminServicePhoneNormalizationTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        db.Tenants.Add(new Tenant { Id = 1, Name = "Main Hospital", IsActive = true });
        db.SaveChanges();
        return db;
    }

    private static AdminService CreateService(AppDbContext db)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(Permissions.ClaimType, Permissions.AdminFull)], "Test"));
        var tenantContext = new Mock<ITenantContextService>();
        tenantContext.Setup(t => t.IsSuperAdmin(It.IsAny<ClaimsPrincipal>())).Returns(true);
        var http = new Mock<IHttpContextAccessor>();
        http.Setup(x => x.HttpContext).Returns(new DefaultHttpContext { User = user });

        return new AdminService(db, NullLogger<AdminService>.Instance,
            tenantContext.Object, http.Object, new Mock<IAuditService>().Object);
    }

    private static CreateEmployeeRequest Request(string? office = null, string? mobile = null) =>
        new(
            AzureAdObjectId: null,
            FirstName: "Test",
            LastName: "Person",
            Email: $"test.{Guid.NewGuid():N}@example.test",
            Title: null, Specialty: null, ClinicalRole: null,
            OfficePhone: office, MobilePhone: mobile, PagerNumber: null,
            OfficeLocation: null, DepartmentId: null, ManagerId: null,
            Certifications: null, Languages: null);

    [Theory]
    [InlineData("(202) 555-0134")]
    [InlineData("202-555-0134")]
    [InlineData("202.555.0134")]
    [InlineData("+1 202 555 0134")]
    public async Task AFormattedNumberIsAcceptedAndStoredAsE164(string typed)
    {
        using var db = CreateDb();

        var created = await CreateService(db).CreateEmployeeAsync(Request(office: typed));

        created.OfficePhone.Should().Be("+12025550134",
            "a number typed the way a human writes it should be normalized, not rejected");
    }

    [Theory]
    [InlineData("x4412")]
    [InlineData("not a phone at all")]
    [InlineData("202-555-0134 x4412")]
    public async Task AnUndialableNumberIsRejected(string typed)
    {
        using var db = CreateDb();
        var service = CreateService(db);

        var act = () => service.CreateEmployeeAsync(Request(office: typed));

        await act.Should().ThrowAsync<ValidationException>();
        db.Employees.Any().Should().BeFalse("a rejected create must not persist anything");
    }

    [Fact]
    public async Task ABlankNumberStaysNull()
    {
        using var db = CreateDb();

        var created = await CreateService(db).CreateEmployeeAsync(Request(office: "", mobile: null));

        created.OfficePhone.Should().BeNull();
        created.MobilePhone.Should().BeNull();
    }

    [Fact]
    public async Task UpdatingNormalizesTheNumberToo()
    {
        using var db = CreateDb();
        var service = CreateService(db);
        var created = await service.CreateEmployeeAsync(Request());

        var updated = await service.UpdateEmployeeAsync(created.Id, new UpdateEmployeeRequest(
            FirstName: "Test", LastName: "Person", Email: created.Email,
            Title: null, Specialty: null, ClinicalRole: null,
            OfficePhone: "(202) 555-0199", MobilePhone: null, PagerNumber: null,
            OfficeLocation: null, DepartmentId: null, ManagerId: null,
            Certifications: null, Languages: null, IsActive: null));

        updated.OfficePhone.Should().Be("+12025550199");
    }
}
