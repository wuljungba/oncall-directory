using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Regressions found by driving the import against real HR-shaped files.
///
/// Each of these reported complete success while doing something wrong, which is what
/// makes them worth pinning: none of them would have shown up as an error the user could
/// see.
/// </summary>
public class BulkImportRegressionTests
{
    private const string Header =
        "azureAdObjectId,firstName,lastName,email,title,officePhone,mobilePhone,officeLocation,departmentId";

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        db.Departments.Add(new Department { Id = 1, Name = "Emergency Medicine", IsActive = true });
        db.SaveChanges();
        return db;
    }

    private static BulkImportService CreateService(AppDbContext db)
        => new(db, NullLogger<BulkImportService>.Instance);

    private static MemoryStream Csv(params string[] lines)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(string.Join("\n", lines) + "\n");
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// A number carrying an extension must be refused, not merged into the number.
    ///
    /// Stripping non-digits folded the extension into the number itself, so
    /// "202-555-0134 x4412" became "+120255501344412" -- fifteen digits, valid E.164, past
    /// the dialable floor. It looked entirely correct and would have been dialled on a code
    /// call. The normalizer's own doc comment says it exists to prevent exactly this.
    /// </summary>
    [Theory]
    [InlineData("202-555-0134 x4412")]
    [InlineData("(202) 555-0134 ext. 4412")]
    [InlineData("+1 202 555 0134 extension 12")]
    [InlineData("2025550134#77")]
    public async Task PhoneWithAnExtensionIsRejected(string phone)
    {
        using var db = CreateDb();

        var result = await CreateService(db).ValidateEmployeesAsync(
            Csv(Header, $",Jane,Smith,jane@hospital.example,Doctor,{phone},,,"));

        result.IsValid.Should().BeFalse(
            "an extension cannot be dialled and must not be merged into the number");
    }

    [Theory]
    [InlineData("(202) 555-0134", "+12025550134")]
    [InlineData("202-555-0134", "+12025550134")]
    [InlineData("+1 202 555 0134", "+12025550134")]
    public async Task OrdinaryNumbersStillNormalize(string phone, string expected)
    {
        using var db = CreateDb();

        var result = await CreateService(db).ImportEmployeesAsync(
            Csv(Header, $",Jane,Smith,jane@hospital.example,Doctor,{phone},,,"));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        (await db.Employees.AsNoTracking().FirstAsync()).OfficePhone.Should().Be(expected);
    }

    /// <summary>
    /// A follow-up file with fewer columns must not erase the columns it omits.
    ///
    /// Every field was assigned unconditionally, and an absent column parsed to null just
    /// like a blank cell, so importing a narrower file reported full success while wiping
    /// title, office phone, location and department. Clearing DepartmentId is the sharp
    /// end: it drops staff out of department-scoped on-call lookups.
    /// </summary>
    [Fact]
    public async Task ANarrowerFileDoesNotEraseTheColumnsItOmits()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        var seeded = await service.ImportEmployeesAsync(
            Csv(Header, "aad-jane,Jane,Smith,jane@hospital.example,Attending,+12025551234,,Floor 3,1"));
        seeded.IsValid.Should().BeTrue(string.Join("; ", seeded.Errors));

        // A narrower file that only means to set the mobile number.
        var result = await service.ImportEmployeesAsync(
            Csv("firstName,lastName,email,mobilePhone",
                "Jane,Smith,jane@hospital.example,(202) 555-0199"));
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));

        var jane = await db.Employees.AsNoTracking()
            .FirstAsync(e => e.Email == "jane@hospital.example");

        jane.MobilePhone.Should().Be("+12025550199", "the column that was supplied is updated");
        jane.Title.Should().Be("Attending", "an omitted column must be left alone");
        jane.OfficePhone.Should().Be("+12025551234");
        jane.OfficeLocation.Should().Be("Floor 3");
        jane.DepartmentId.Should().Be(1,
            "detaching staff from their department silently is the dangerous case");
    }

    /// <summary>A blank cell still means "clear this", which is different from omitting it.</summary>
    [Fact]
    public async Task ABlankCellInASuppliedColumnStillClearsTheValue()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        await service.ImportEmployeesAsync(
            Csv(Header, "aad-jane,Jane,Smith,jane@hospital.example,Attending,,,Floor 3,1"));

        await service.ImportEmployeesAsync(
            Csv(Header, "aad-jane,Jane,Smith,jane@hospital.example,,,,,"));

        var jane = await db.Employees.AsNoTracking()
            .FirstAsync(e => e.Email == "jane@hospital.example");
        jane.Title.Should().BeEmpty();
        jane.DepartmentId.Should().BeNull();
    }

    /// <summary>
    /// Emails differing only in case are one person.
    ///
    /// The in-file duplicate check was case-insensitive but the database lookup was not, so
    /// on SQLite a re-import with different casing created a second record for the same
    /// clinician, while SQL Server would instead reject the insert outright. Both wrong.
    /// </summary>
    [Fact]
    public async Task AnEmailDifferingOnlyByCaseUpdatesRatherThanDuplicates()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        await service.ImportEmployeesAsync(
            Csv(Header, ",Jane,Smith,jane.smith@hospital.example,Attending,,,,"));
        await service.ImportEmployeesAsync(
            Csv(Header, ",Jane,Smith,JANE.SMITH@HOSPITAL.EXAMPLE,Chief,,,,"));

        db.Employees.Count().Should().Be(1,
            "one clinician must not become two records because of letter case");
    }

    /// <summary>
    /// A record with no tenant is unclaimed, not another organization's.
    ///
    /// Treating null as foreign made every employee created by the app's own onboarding
    /// path (which leaves TenantId null) permanently un-importable by a scoped admin, with
    /// a message claiming it belonged to someone else.
    /// </summary>
    [Fact]
    public async Task AnUnclaimedRecordIsNotReportedAsBelongingToAnotherOrganization()
    {
        using var db = CreateDb();
        db.Employees.Add(new Employee
        {
            Id = Guid.NewGuid(),
            AzureAdObjectId = "csv-import-existing",
            FirstName = "Jane",
            LastName = "Smith",
            Email = "jane.smith@hospital.example",
            TenantId = null,
            IsActive = true,
        });
        db.SaveChanges();

        var result = await CreateService(db).ImportEmployeesAsync(
            Csv(Header, ",Jane,Smith,jane.smith@hospital.example,Chief,,,,"),
            tenantId: 1,
            allowedTenantIds: [1]);

        result.Errors.Should().NotContain(e => e.Contains("another organization"));
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }
}
