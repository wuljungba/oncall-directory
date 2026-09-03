using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// A unit or service line reached by phone -- "3North", x3434 -- could not be stored at
/// all: Employee.Email was required and uniquely indexed, so the first such contact was
/// rejected outright and the second collided with the first.
///
/// These pin the two halves of the fix that are easy to undo by accident: the index filter
/// (without it, one email-less contact is the most any directory can hold) and the rule
/// that a row with no address is nobody -- it must never be resolved as the identity
/// behind a sign-in or a permission grant.
/// </summary>
public class DepartmentContactTests
{
    private static BulkImportService CreateService(AppDbContext db)
        => new(db, NullLogger<BulkImportService>.Instance);

    private static AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Built on SQLite, not the in-memory provider, because the in-memory provider does
    /// not enforce indexes at all -- it would report success no matter what the filter
    /// said, which is precisely the thing under test here.
    /// </summary>
    private static (AppDbContext Db, SqliteConnection Connection) CreateRelationalDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return (db, connection);
    }

    private static Employee Unit(string label, string extension) => new()
    {
        Id = Guid.NewGuid(),
        AzureAdObjectId = $"unit-{label}",
        ContactType = ContactType.Department,
        DisplayName = label,
        Extension = extension,
        IsActive = true,
        Source = "Local",
    };

    // ── The index filter ──

    [Fact]
    public async Task SeveralContactsMayShareHavingNoEmail()
    {
        var (db, connection) = CreateRelationalDb();
        using (connection)
        using (db)
        {
            db.Employees.Add(Unit("3North", "3434"));
            db.Employees.Add(Unit("4West", "3455"));
            db.Employees.Add(Unit("ICU", "3400"));

            var save = async () => await db.SaveChangesAsync();

            await save.Should().NotThrowAsync(
                "the unique index on Email is filtered to non-null rows, so contacts "
                + "without an address do not collide with one another");

            (await db.Employees.CountAsync()).Should().Be(3);
        }
    }

    [Fact]
    public async Task TwoPeopleStillMayNotShareAnEmail()
    {
        var (db, connection) = CreateRelationalDb();
        using (connection)
        using (db)
        {
            db.Employees.Add(new Employee
            {
                Id = Guid.NewGuid(), AzureAdObjectId = "a",
                FirstName = "Jane", LastName = "Smith", Email = "jane@hospital.example",
            });
            db.Employees.Add(new Employee
            {
                Id = Guid.NewGuid(), AzureAdObjectId = "b",
                FirstName = "Jane", LastName = "Smyth", Email = "jane@hospital.example",
            });

            var save = async () => await db.SaveChangesAsync();

            await save.Should().ThrowAsync<DbUpdateException>(
                "filtering the index must not cost the guarantee that stops one clinician "
                + "being imported twice");
        }
    }

    // ── Importing one ──

    [Fact]
    public async Task ImportsAUnitWithNoNameAndNoEmail()
    {
        using var db = CreateInMemoryDb();

        var csv = "displayName,officePhone,extension\n"
                + "3North,845-568-3434,x3434";

        var result = await CreateService(db).ImportEmployeesAsync(ToStream(csv));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));

        var contact = await db.Employees.AsNoTracking().SingleAsync();
        contact.ContactType.Should().Be(ContactType.Department);
        contact.DisplayName.Should().Be("3North");
        contact.Email.Should().BeNull();
        contact.Extension.Should().Be("3434");
        contact.OfficePhone.Should().Be("+18455683434");
    }

    [Fact]
    public async Task AUnitWithNothingToDialIsRefused()
    {
        using var db = CreateInMemoryDb();

        var csv = "displayName,contactType,officePhone,extension\n"
                + "3North,Department,,";

        var result = await CreateService(db).ImportEmployeesAsync(ToStream(csv));

        result.IsValid.Should().BeFalse(
            "a unit that dials nowhere is worse than absent -- it looks like a route");
        result.Errors.Should().Contain(e => e.Contains("3North"));
    }

    [Fact]
    public async Task APersonWithNoEmailIsStillRefused()
    {
        using var db = CreateInMemoryDb();

        var csv = "firstName,lastName,officePhone\n"
                + "Jane,Smith,845-568-3434";

        var result = await CreateService(db).ImportEmployeesAsync(ToStream(csv));

        result.IsValid.Should().BeFalse(
            "email stays required for a person; only a department contact may omit it");
        result.Errors.Should().Contain(e => e.Contains("email is required"));
    }

    /// <summary>
    /// Twelve units in one sheet must import as twelve rows. Deduplicating by email would
    /// match them all to each other on a shared absence of one, leaving a single row.
    /// </summary>
    [Fact]
    public async Task ManyUnitsInOneFileDoNotCollapseIntoOne()
    {
        using var db = CreateInMemoryDb();

        var csv = "displayName,extension\n"
                + "3North,3434\n4West,3455\nICU,3400\nPACU,3401";

        var result = await CreateService(db).ImportEmployeesAsync(ToStream(csv));

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        (await db.Employees.CountAsync()).Should().Be(4);
    }

    // ── A row with no address is nobody ──

    [Fact]
    public async Task AUnitIsNeverAdoptedAsTheIdentityBehindASignIn()
    {
        using var db = CreateInMemoryDb();
        db.Employees.Add(Unit("3North", "3434"));
        await db.SaveChangesAsync();

        var account = await CreateAccountService(db).RegisterAsync(
            "newcomer@hospital.example", "correct horse battery staple", "Newcomer");

        account.EmployeeId.Should().BeNull(
            "an email-less contact must not be matched by a blank comparison and handed "
            + "someone's shifts and directory presence");
    }

    // ── Finding one ──

    [Theory]
    [InlineData("3North")]
    [InlineData("3 North")]
    [InlineData("3-North")]
    [InlineData("3north")]
    public async Task AUnitIsFoundHoweverItsNameIsTyped(string query)
    {
        using var db = CreateInMemoryDb();
        db.Employees.Add(Unit("3 North", "3434"));
        await db.SaveChangesAsync();

        var directory = new DirectoryService(db, TestTenantScopes.Unrestricted);

        var results = await directory.SearchEmployeesAsync(query);

        results.Should().HaveCount(1, $"'{query}' should find the unit whatever spacing it was stored with");
        results[0].DisplayName.Should().Be("3 North");
    }

    [Fact]
    public async Task AUnitIsFoundByItsExtension()
    {
        using var db = CreateInMemoryDb();
        db.Employees.Add(Unit("3North", "3434"));
        await db.SaveChangesAsync();

        var results = await new DirectoryService(db, TestTenantScopes.Unrestricted)
            .SearchEmployeesAsync("3434");

        results.Should().HaveCount(1);
    }

    [Fact]
    public async Task LookupByEmailIgnoresContactsThatHaveNone()
    {
        using var db = CreateInMemoryDb();
        db.Employees.Add(Unit("3North", "3434"));
        await db.SaveChangesAsync();

        var directory = new DirectoryService(db, TestTenantScopes.Unrestricted);

        (await directory.GetEmployeeByEmailAsync("")).Should().BeNull();
        (await directory.GetEmployeeByEmailAsync("   ")).Should().BeNull();
        (await directory.GetEmployeeByEmailAsync("nobody@hospital.example")).Should().BeNull();
    }

    // ── Helpers ──

    private static MemoryStream ToStream(string csv)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(csv);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    private static LocalAccountService CreateAccountService(AppDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Local:SigningKey"] = "test-signing-key-long-enough-for-hmac-sha256",
            })
            .Build();

        var jwt = new OnCallApi.Authentication.LocalJwtService(
            config, NullLogger<OnCallApi.Authentication.LocalJwtService>.Instance,
            new StubHostEnvironment { EnvironmentName = "Development" });

        return new LocalAccountService(
            db, jwt,
            Options.Create(new SuperAdminOptions()),
            NullLogger<LocalAccountService>.Instance);
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // ── A unit line whose label looks like a person's name ──

    /// <summary>
    /// "ICU Desk" parses cleanly into a first and last name, so it was treated as a
    /// person and rejected for having no email — and because a commit is all or nothing,
    /// one such row failed the entire file. A real hospital list is full of them: Blood
    /// Bank, Main Switchboard, Emergency Department.
    ///
    /// Only the missing mailbox distinguishes a place from a person here, so the row is
    /// claimed as a unit line and FLAGGED, never silently relabelled: a member of staff
    /// who simply has no email address looks exactly the same, and only the person doing
    /// the import can tell them apart.
    /// </summary>
    [Theory]
    [InlineData("ICU Desk")]
    [InlineData("Blood Bank")]
    [InlineData("Main Switchboard")]
    [InlineData("Emergency Department")]
    public void ATwoWordUnitLabelWithNoEmailImportsAsAUnitAndIsFlagged(string label)
    {
        var (record, error) = BulkImportService.ParseEmployeeRow(new Dictionary<string, string>
        {
            ["name"] = label,
            ["officePhone"] = "845-568-3400",
        });

        error.Should().BeNull("a unit line with a phone number is importable, not an error");
        record.Should().NotBeNull();
        record!.ContactType.Should().Be(ContactType.Department);
        record.DisplayName.Should().Be(label);
        record.Email.Should().BeNull();
        record.ReviewReason.Should().NotBeNull(
            "the same row could be a person with no email address, so a human confirms it");
    }

    /// <summary>
    /// The unambiguous case keeps its silence. "3North" needs no review note, and adding
    /// one to every unit line would bury the rows that genuinely need looking at.
    /// </summary>
    [Fact]
    public void AnUnambiguousUnitLabelIsNotFlaggedForReview()
    {
        var (record, error) = BulkImportService.ParseEmployeeRow(new Dictionary<string, string>
        {
            ["name"] = "3North",
            ["officePhone"] = "845-568-3434",
            ["extension"] = "x3434",
        });

        error.Should().BeNull();
        record!.ContactType.Should().Be(ContactType.Department);
        record.ReviewReason.Should().BeNull();
    }

    /// <summary>
    /// A person with an email address is still a person, however their name is spelled.
    /// </summary>
    [Fact]
    public void ATwoWordNameWithAnEmailIsStillAPerson()
    {
        var (record, error) = BulkImportService.ParseEmployeeRow(new Dictionary<string, string>
        {
            ["name"] = "Jane Roe",
            ["email"] = "jane.roe@hospital.example",
            ["officePhone"] = "202-555-0134",
        });

        error.Should().BeNull();
        record!.ContactType.Should().Be(ContactType.Person);
        record.FirstName.Should().Be("Jane");
        record.LastName.Should().Be("Roe");
        record.ReviewReason.Should().BeNull();
    }
}
