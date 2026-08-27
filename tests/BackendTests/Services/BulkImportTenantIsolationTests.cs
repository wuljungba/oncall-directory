using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// A bulk import must never reach across tenants.
///
/// It used to. FindExistingEmployeeAsync matched existing employees with no tenant filter,
/// and the update branch then reassigned TenantId to the importer's tenant. So a scoped
/// admin at one hospital who uploaded a file containing another hospital's employee email
/// silently moved that person into their own directory and overwrote their name, title and
/// phone numbers -- the other hospital simply lost the record.
///
/// The destination tenant was always forced to the caller's own, which is exactly what made
/// the takeover land rather than prevent it.
/// </summary>
public class BulkImportTenantIsolationTests
{
    private const int TenantA = 1;
    private const int TenantB = 2;
    private static readonly Guid CarolId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    /// <summary>Two tenants, each with one employee. Carol belongs to tenant B.</summary>
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        db.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Main Hospital", IsActive = true },
            new Tenant { Id = TenantB, Name = "North Campus", IsActive = true });
        db.Departments.Add(new Department { Id = 1, Name = "Emergency Medicine", TenantId = TenantA });
        db.Employees.Add(new Employee
        {
            Id = CarolId,
            AzureAdObjectId = "aad-carol",
            FirstName = "Carol",
            LastName = "Nguyen",
            Email = "carol@north.example",
            Title = "Charge Nurse",
            MobilePhone = "+15185550100",
            TenantId = TenantB,
            IsActive = true,
        });
        db.SaveChanges();
        return db;
    }

    private static BulkImportService CreateService(AppDbContext db)
        => new(db, NullLogger<BulkImportService>.Instance);

    private static MemoryStream CsvToStream(string csv)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(csv);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    /// <summary>A scoped admin at tenant A uploads a file naming tenant B's employee.</summary>
    private const string CarolTakeoverCsv =
        "azureAdObjectId,firstName,lastName,email,title,officePhone,mobilePhone,officeLocation,departmentId\n"
        + ",Mallory,Impostor,carol@north.example,Intern,,,,\n";

    [Fact]
    public async Task ImportingAnotherTenantsEmail_DoesNotMoveTheEmployee()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);

        await service.ImportEmployeesAsync(
            CsvToStream(CarolTakeoverCsv), tenantId: TenantA, allowedTenantIds: [TenantA]);

        var carol = await db.Employees.AsNoTracking().FirstAsync(e => e.Id == CarolId);
        carol.TenantId.Should().Be(TenantB, "an import must never move another tenant's employee");
    }

    [Fact]
    public async Task ImportingAnotherTenantsEmail_DoesNotOverwriteTheirDetails()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);

        await service.ImportEmployeesAsync(
            CsvToStream(CarolTakeoverCsv), tenantId: TenantA, allowedTenantIds: [TenantA]);

        var carol = await db.Employees.AsNoTracking().FirstAsync(e => e.Id == CarolId);
        carol.FirstName.Should().Be("Carol");
        carol.LastName.Should().Be("Nguyen");
        carol.Title.Should().Be("Charge Nurse");
        carol.MobilePhone.Should().Be("+15185550100");
    }

    /// <summary>
    /// The email is globally unique, so the row cannot be created either. What matters is
    /// that it fails loudly instead of quietly stealing the existing record.
    /// </summary>
    [Fact]
    public async Task ImportingAnotherTenantsEmail_DoesNotSilentlySucceed()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);

        var result = await service.ImportEmployeesAsync(
            CsvToStream(CarolTakeoverCsv), tenantId: TenantA, allowedTenantIds: [TenantA]);

        db.Employees.Count(e => e.Email == "carol@north.example")
            .Should().Be(1, "the import must not create a second copy of a globally unique email");
        result.Imported.Should().Be(0);
    }

    /// <summary>The legitimate case must keep working: your own tenant's records still update.</summary>
    [Fact]
    public async Task ImportingYourOwnTenantsEmployee_StillUpdatesThem()
    {
        using var db = CreateDbContext();
        var aliceId = Guid.NewGuid();
        db.Employees.Add(new Employee
        {
            Id = aliceId,
            AzureAdObjectId = "aad-alice",
            FirstName = "Alice",
            LastName = "Adams",
            Email = "alice@main.example",
            Title = "Attending",
            TenantId = TenantA,
            IsActive = true,
        });
        db.SaveChanges();

        var service = CreateService(db);
        var csv = "azureAdObjectId,firstName,lastName,email,title,officePhone,mobilePhone,officeLocation,departmentId\n"
                + "aad-alice,Alice,Adams,alice@main.example,Chief Attending,,,,\n";

        var result = await service.ImportEmployeesAsync(
            CsvToStream(csv), tenantId: TenantA, allowedTenantIds: [TenantA]);

        result.IsValid.Should().BeTrue();
        var alice = await db.Employees.AsNoTracking().FirstAsync(e => e.Id == aliceId);
        alice.Title.Should().Be("Chief Attending");
        alice.TenantId.Should().Be(TenantA);
    }

    /// <summary>A super admin (null allow-list) is intentionally unrestricted.</summary>
    [Fact]
    public async Task SuperAdmin_CanStillMatchAcrossTenants()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,title,officePhone,mobilePhone,officeLocation,departmentId\n"
                + "aad-carol,Carol,Nguyen,carol@north.example,Nurse Manager,,,,\n";

        var result = await service.ImportEmployeesAsync(
            CsvToStream(csv), tenantId: null, allowedTenantIds: null);

        result.IsValid.Should().BeTrue();
        var carol = await db.Employees.AsNoTracking().FirstAsync(e => e.Id == CarolId);
        carol.Title.Should().Be("Nurse Manager");
        carol.TenantId.Should().Be(TenantB, "even a super admin update must not relocate the record");
    }
}
