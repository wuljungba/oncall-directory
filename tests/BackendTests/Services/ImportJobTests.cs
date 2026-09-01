using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services.Import;

namespace BackendTests.Services;

/// <summary>
/// A staff upload is routinely a workbook with one sheet per unit, floor or service line.
/// Only the first sheet was ever read, so eleven of twelve were dropped in silence -- the
/// import reported success, and nothing in the result said most of the file had been
/// ignored.
///
/// These are built on real .xlsx workbooks rather than CSV, because the sheet enumeration
/// is the thing under test and a CSV cannot exercise it.
/// </summary>
public class ImportJobTests
{
    private static AppDbContext CreateDb(params string[] departments)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var id = 1;
        foreach (var name in departments)
            db.Departments.Add(new Department { Id = id++, Name = name, IsActive = true, TenantId = 1 });

        db.SaveChanges();
        return db;
    }

    private static ImportJobService CreateService(AppDbContext db)
        => new(db, NullLogger<ImportJobService>.Instance);

    // ── Reading every sheet ──

    [Fact]
    public async Task EverySheetIsRead()
    {
        using var db = CreateDb();

        var workbook = Workbook(
            ("3North", ["firstName,lastName,email", "Jane,Smith,jane@hospital.example"]),
            ("4West", ["firstName,lastName,email", "John,Doe,john@hospital.example"]),
            ("ICU", ["firstName,lastName,email", "Ada,Lovelace,ada@hospital.example"]));

        var (job, error) = await CreateService(db).CreateAsync(
            workbook, "roster.xlsx", null, "Tester", "tester@hospital.example");

        error.Should().BeNull();
        job!.SheetCount.Should().Be(3, "all three unit sheets must be read, not just the first");
        job.TotalRows.Should().Be(3);
    }

    [Fact]
    public async Task ThreeSheetsWithTheSameHeadersCommitAsOneDirectory()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        var workbook = Workbook(
            ("3North", ["firstName,lastName,email", "Jane,Smith,jane@hospital.example"]),
            ("4West", ["firstName,lastName,email", "John,Doe,john@hospital.example"]),
            ("ICU", ["firstName,lastName,email", "Ada,Lovelace,ada@hospital.example"]));

        var (job, _) = await service.CreateAsync(workbook, "roster.xlsx", null, "Tester", "t@x.example");

        var (result, error) = await service.CommitAsync(job!, null);

        error.Should().BeNull();
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.Imported.Should().Be(3);
        (await db.Employees.CountAsync()).Should().Be(3);
    }

    /// <summary>
    /// A cover page or a notes tab is normal in a real workbook. Failing the whole upload
    /// over one would block a file that is otherwise perfectly good.
    /// </summary>
    [Fact]
    public async Task ASheetWithNoHeaderRowIsSkippedNotFatal()
    {
        using var db = CreateDb();

        var workbook = Workbook(
            ("Instructions", []),
            ("Staff", ["firstName,lastName,email", "Jane,Smith,jane@hospital.example"]));

        var (job, error) = await CreateService(db).CreateAsync(
            workbook, "roster.xlsx", null, "Tester", "t@x.example");

        error.Should().BeNull();
        job!.SheetCount.Should().Be(1, "the empty tab is skipped, and the staff sheet still imports");
        job.TotalRows.Should().Be(1);
    }

    [Fact]
    public async Task AWorkbookWithNoUsableSheetIsReportedNotStaged()
    {
        using var db = CreateDb();

        var (job, error) = await CreateService(db).CreateAsync(
            Workbook(("Cover", [])), "empty.xlsx", null, "Tester", "t@x.example");

        job.Should().BeNull();
        error.Should().NotBeNull();
    }

    // ── Nothing is written before commit ──

    [Fact]
    public async Task StagingWritesNothingToTheDirectory()
    {
        using var db = CreateDb();

        await CreateService(db).CreateAsync(
            Workbook(("Staff", ["firstName,lastName,email", "Jane,Smith,jane@hospital.example"])),
            "roster.xlsx", null, "Tester", "t@x.example");

        (await db.Employees.CountAsync()).Should().Be(0,
            "a preview that has already written the rows is not a preview");
    }

    // ── Bad rows ──

    [Fact]
    public async Task OneBadRowDoesNotCostTheOtherThree()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        var workbook = Workbook(("Staff",
        [
            "firstName,lastName,email",
            "Jane,Smith,jane@hospital.example",
            "Broken,,",
            "John,Doe,john@hospital.example",
            "Ada,Lovelace,ada@hospital.example",
        ]));

        var (job, _) = await service.CreateAsync(workbook, "roster.xlsx", null, "Tester", "t@x.example");

        var preview = await service.BuildPreviewAsync(job!, null);
        preview.ErrorCount.Should().Be(1);
        preview.ReadyCount.Should().Be(3);

        // The commit refuses while a bad row is still included -- the user excludes it.
        var bad = preview.Rows.Single(r => r.ErrorReason != null);
        await service.SetResolutionAsync(job!, bad.Id, ImportRowResolution.Skip);

        var (result, _) = await service.CommitAsync(job!, null);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.Imported.Should().Be(3);
    }

    [Fact]
    public async Task ACommitIsRefusedWhileAnIncludedRowIsBroken()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        var workbook = Workbook(("Staff",
        [
            "firstName,lastName,email",
            "Jane,Smith,jane@hospital.example",
            "Broken,,",
        ]));

        var (job, _) = await service.CreateAsync(workbook, "roster.xlsx", null, "Tester", "t@x.example");
        var (result, _) = await service.CommitAsync(job!, null);

        result.IsValid.Should().BeFalse();
        (await db.Employees.CountAsync()).Should().Be(0,
            "a half-written directory is worse than a refused one -- nobody can tell which half arrived");
    }

    [Fact]
    public async Task TheErrorReportHandsBackTheRowsAsUploaded()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        var workbook = Workbook(("Staff",
        [
            "firstName,lastName,email",
            "Broken,,",
        ]));

        var (job, _) = await service.CreateAsync(workbook, "roster.xlsx", null, "Tester", "t@x.example");
        await service.BuildPreviewAsync(job!, null);

        var csv = System.Text.Encoding.UTF8.GetString(await service.BuildErrorReportAsync(job!));

        csv.Should().Contain("Problem");
        csv.Should().Contain("Broken", "the original row must come back, so the fix is to correct this file");
    }

    // ── Duplicates within one upload ──

    [Fact]
    public async Task TheSameAddressOnTwoSheetsIsReportedByRowNotByTheDatabase()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        var workbook = Workbook(
            ("3North", ["firstName,lastName,email", "Jane,Smith,jane@hospital.example"]),
            ("4West", ["firstName,lastName,email", "Jane,Smith,jane@hospital.example"]));

        var (job, _) = await service.CreateAsync(workbook, "roster.xlsx", null, "Tester", "t@x.example");
        var preview = await service.BuildPreviewAsync(job!, null);

        preview.ErrorCount.Should().Be(1, "the first row is fine; the second one repeats it");
        preview.Rows.Should().Contain(r => r.ErrorReason != null && r.ErrorReason.Contains("3North"));
    }

    // ── Matching what is already there ──

    [Fact]
    public async Task AnExistingPersonIsOfferedAsAMergeNotADuplicate()
    {
        using var db = CreateDb();
        db.Employees.Add(new Employee
        {
            Id = Guid.NewGuid(),
            AzureAdObjectId = "existing-1",
            FirstName = "Jane",
            LastName = "Smith",
            Email = "jane@hospital.example",
            TenantId = 1,
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var workbook = Workbook(("Staff",
            ["firstName,lastName,email,title", "Jane,Smith,jane@hospital.example,Attending"]));

        var (job, _) = await service.CreateAsync(workbook, "roster.xlsx", 1, "Tester", "t@x.example");
        var preview = await service.BuildPreviewAsync(job!, [1]);

        var row = preview.Rows.Single();
        row.MatchedOn.Should().Be("email");
        row.Resolution.Should().Be(ImportRowResolution.Merge,
            "creating a second record for somebody already here is how a code call reaches "
            + "the number nobody answers");

        var (result, _) = await service.CommitAsync(job!, [1]);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        (await db.Employees.CountAsync()).Should().Be(1, "the existing person was updated, not duplicated");
        (await db.Employees.SingleAsync()).Title.Should().Be("Attending");
    }

    /// <summary>
    /// The isolation rule the single-file path already enforces has to hold through the
    /// staged path too, or the new endpoints are a way around it.
    /// </summary>
    [Fact]
    public async Task AnotherTenantsRecordIsNeverMatched()
    {
        using var db = CreateDb();
        db.Employees.Add(new Employee
        {
            Id = Guid.NewGuid(),
            AzureAdObjectId = "other-tenant",
            FirstName = "Jane",
            LastName = "Smith",
            Email = "jane@hospital.example",
            TenantId = 99,
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var workbook = Workbook(("Staff",
            ["firstName,lastName,email", "Jane,Smith,jane@hospital.example"]));

        var (job, _) = await service.CreateAsync(workbook, "roster.xlsx", 1, "Tester", "t@x.example");
        var preview = await service.BuildPreviewAsync(job!, [1]);

        preview.Rows.Single().MatchedOn.Should().BeNull(
            "a scoped admin must not resolve, and then overwrite, another customer's record");
    }

    // ── Mapping ──

    [Fact]
    public async Task AMappingCanBeAppliedToEverySheetAtOnce()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        // "Contact" is not a heading the aliases know, so it is ignored until mapped.
        var workbook = Workbook(
            ("3North", ["firstName,lastName,Contact", "Jane,Smith,jane@hospital.example"]),
            ("4West", ["firstName,lastName,Contact", "John,Doe,john@hospital.example"]));

        var (job, _) = await service.CreateAsync(workbook, "roster.xlsx", null, "Tester", "t@x.example");

        var before = await service.BuildPreviewAsync(job!, null);
        before.ErrorCount.Should().Be(2, "with no email column mapped, neither row can be a person");

        await service.UpdateMappingAsync(
            job!, "3North", new Dictionary<string, string> { ["Contact"] = "email" },
            applyToAllSheets: true, excludedSheets: null);

        var after = await service.BuildPreviewAsync(job!, null);
        after.ErrorCount.Should().Be(0, "one mapping covers both sheets, which is why the file has sheets");
        after.ReadyCount.Should().Be(2);
    }

    [Fact]
    public async Task AnExcludedSheetContributesNothing()
    {
        using var db = CreateDb();
        var service = CreateService(db);

        var workbook = Workbook(
            ("Staff", ["firstName,lastName,email", "Jane,Smith,jane@hospital.example"]),
            ("Old Staff", ["firstName,lastName,email", "John,Doe,john@hospital.example"]));

        var (job, _) = await service.CreateAsync(workbook, "roster.xlsx", null, "Tester", "t@x.example");

        await service.UpdateMappingAsync(
            job!, "Staff", new Dictionary<string, string>(),
            applyToAllSheets: false, excludedSheets: ["Old Staff"]);

        var (result, _) = await service.CommitAsync(job!, null);

        result.Imported.Should().Be(1);
        (await db.Employees.SingleAsync()).LastName.Should().Be("Smith");
    }

    /// <summary>
    /// A workbook with one sheet per unit writes the unit in the TAB, not in a column.
    /// It is used only when the tab actually names a department that exists -- defaulting
    /// to it unconditionally would fail every row of a file whose tab is "Sheet1".
    /// </summary>
    [Fact]
    public async Task TheSheetNameFillsInAMissingDepartment()
    {
        using var db = CreateDb("Cardiology");
        var service = CreateService(db);

        var workbook = Workbook(
            ("Cardiology", ["firstName,lastName,email", "Jane,Smith,jane@hospital.example"]));

        var (job, _) = await service.CreateAsync(workbook, "roster.xlsx", 1, "Tester", "t@x.example");
        var (result, _) = await service.CommitAsync(job!, [1]);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        (await db.Employees.SingleAsync()).DepartmentId.Should().Be(1);
    }

    [Fact]
    public async Task ASheetNameThatIsNotADepartmentIsIgnoredRatherThanFailingEveryRow()
    {
        using var db = CreateDb("Cardiology");
        var service = CreateService(db);

        var workbook = Workbook(
            ("Sheet1", ["firstName,lastName,email", "Jane,Smith,jane@hospital.example"]));

        var (job, _) = await service.CreateAsync(workbook, "roster.xlsx", 1, "Tester", "t@x.example");
        var (result, _) = await service.CommitAsync(job!, [1]);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        (await db.Employees.SingleAsync()).DepartmentId.Should().BeNull();
    }

    // ── Building a workbook ──

    /// <summary>
    /// Builds a real .xlsx in memory. Each sheet is given as comma-separated lines for
    /// readability; the first line is the header row.
    /// </summary>
    private static MemoryStream Workbook(params (string Name, string[] Lines)[] sheets)
    {
        var stream = new MemoryStream();

        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheetElements = new Sheets();

            uint sheetId = 1;
            foreach (var (name, lines) in sheets)
            {
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();

                uint rowIndex = 1;
                foreach (var line in lines)
                {
                    var row = new Row { RowIndex = rowIndex };
                    var column = 'A';

                    foreach (var value in line.Split(','))
                    {
                        row.Append(new Cell
                        {
                            CellReference = $"{column}{rowIndex}",
                            DataType = CellValues.String,
                            CellValue = new CellValue(value),
                        });
                        column++;
                    }

                    sheetData.Append(row);
                    rowIndex++;
                }

                worksheetPart.Worksheet = new Worksheet(sheetData);

                sheetElements.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = sheetId++,
                    Name = name,
                });
            }

            workbookPart.Workbook.Append(sheetElements);
        }

        stream.Position = 0;
        return stream;
    }
}
