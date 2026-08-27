using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// Covers what an uploaded file may actually BE, as opposed to what its rows say.
///
/// The case that prompted these: a hospital HR export saved as an Excel workbook but named
/// "staff.csv". It was fed to the CSV line splitter, whose header came out of the zip's
/// binary preamble, so an 85-row file produced 85 meaningless errors and imported nobody.
/// Format is now decided by content — the import service is handed a Stream and never sees
/// a file name, so the extension cannot mislead it either way.
/// </summary>
public class BulkImportFormatTests
{
    private const string Headers =
        "azureAdObjectId,firstName,lastName,email,title,officePhone,mobilePhone,officeLocation,departmentId";

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        db.Departments.Add(new Department { Id = 1, Name = "Emergency Medicine" });
        db.Departments.Add(new Department { Id = 2, Name = "Cardiology" });
        db.SaveChanges();
        return db;
    }

    private static BulkImportService CreateService(AppDbContext db) =>
        new(db, NullLogger<BulkImportService>.Instance);

    private static MemoryStream CsvToStream(string csv)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(csv);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    // ── Excel workbooks ──

    [Fact]
    public async Task ImportEmployees_Xlsx_ImportsSameRowsAsEquivalentCsv()
    {
        List<string?[]> grid =
        [
            Headers.Split(','),
            ["", "Jane", "Smith", "jane@test.com", "Physician", "+12025551234", "", "Floor 3", "1"],
            ["", "Alan", "Reyes", "alan@test.com", "Surgeon", "+12025554321", "", "Floor 4", "2"],
        ];

        var db = CreateDbContext();
        var result = await CreateService(db).ImportEmployeesAsync(XlsxToStream(grid));

        result.Errors.Should().BeEmpty();
        result.IsValid.Should().BeTrue();
        result.Imported.Should().Be(2);

        var saved = await db.Employees.OrderBy(e => e.LastName).ToListAsync();
        saved.Should().HaveCount(2);
        saved[0].FirstName.Should().Be("Alan");
        saved[0].DepartmentId.Should().Be(2);
        saved[1].FirstName.Should().Be("Jane");
        saved[1].OfficeLocation.Should().Be("Floor 3");
    }

    /// <summary>
    /// The one that matters most. Excel omits empty cells from the XML entirely, so reading
    /// cells in document order shifts every later value one column left — which here would
    /// give an employee someone else's phone number while reporting a clean import.
    /// </summary>
    [Fact]
    public async Task ImportEmployees_XlsxWithBlankCellsMidRow_KeepsColumnAlignment()
    {
        // null = the cell is absent from the file, exactly as Excel writes a blank.
        List<string?[]> grid =
        [
            Headers.Split(','),
            [null, "Jane", "Smith", "jane@test.com", null, null, "+12025559999", "Floor 3", "1"],
        ];

        var db = CreateDbContext();
        var result = await CreateService(db).ImportEmployeesAsync(XlsxToStream(grid));

        result.Errors.Should().BeEmpty();
        result.Imported.Should().Be(1);

        var saved = await db.Employees.SingleAsync();
        saved.FirstName.Should().Be("Jane");
        saved.LastName.Should().Be("Smith");
        saved.Email.Should().Be("jane@test.com");
        saved.Title.Should().BeEmpty();   // blank text column, same as the CSV path
        saved.OfficePhone.Should().BeNull(); // blank phone column maps to null
        // The value that would have slid into officePhone had the blanks collapsed.
        saved.MobilePhone.Should().Be("+12025559999");
        saved.OfficeLocation.Should().Be("Floor 3");
        saved.DepartmentId.Should().Be(1);
    }

    [Fact]
    public async Task ImportEmployees_XlsxTrailingBlankCells_IsNotReportedAsRaggedRow()
    {
        // officeLocation and departmentId absent: a trailing blank is a present-but-empty
        // value, not a short row, so it must not trip the column-count check.
        List<string?[]> grid =
        [
            Headers.Split(','),
            [null, "Jane", "Smith", "jane@test.com", "Physician", "+12025551234", null, null, null],
        ];

        var db = CreateDbContext();
        var result = await CreateService(db).ImportEmployeesAsync(XlsxToStream(grid));

        result.Errors.Should().BeEmpty();
        result.Imported.Should().Be(1);
    }

    [Fact]
    public async Task ImportEmployees_XlsxWithSharedStrings_ResolvesCellText()
    {
        // Real Excel stores repeated text in a shared string table rather than inline.
        List<string?[]> grid =
        [
            Headers.Split(','),
            ["", "Jane", "Smith", "jane@test.com", "Physician", "", "", "Floor 3", "1"],
        ];

        var db = CreateDbContext();
        var result = await CreateService(db).ImportEmployeesAsync(XlsxToStream(grid, useSharedStrings: true));

        result.Errors.Should().BeEmpty();
        var saved = await db.Employees.SingleAsync();
        saved.Email.Should().Be("jane@test.com");
        saved.Title.Should().Be("Physician");
    }

    [Fact]
    public async Task ValidateEmployees_XlsxWithNoRows_ReportsNothingToImport()
    {
        List<string?[]> grid = [Headers.Split(',')];

        var result = await CreateService(CreateDbContext()).ValidateEmployeesAsync(XlsxToStream(grid));

        result.TotalRows.Should().Be(0);
        result.Errors.Should().BeEmpty();
    }

    // ── Files that are not spreadsheets at all ──

    [Fact]
    public async Task ImportEmployees_LegacyXlsWorkbook_ReturnsOneActionableError()
    {
        // OLE2 compound-file magic — a real .xls, or an .xls renamed to .csv.
        var ole2 = new byte[512];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(ole2, 0);

        var result = await CreateService(CreateDbContext()).ImportEmployeesAsync(new MemoryStream(ole2));

        result.IsValid.Should().BeFalse();
        result.Imported.Should().Be(0);
        result.Errors.Should().ContainSingle()
            .Which.Should().Contain(".xls").And.Contain("Save As");
    }

    [Fact]
    public async Task ImportEmployees_BinaryFile_ReturnsOneActionableError()
    {
        // A PNG renamed .csv, and anything else with NUL bytes in it.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };

        var result = await CreateService(CreateDbContext()).ImportEmployeesAsync(new MemoryStream(png));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Contain("not a CSV or an Excel workbook");
    }

    [Fact]
    public async Task ImportEmployees_ZipThatIsNotAWorkbook_ReturnsOneActionableError()
    {
        var zip = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
            zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("notes.txt").Open();
            entry.Write("nothing to see here"u8);
        }
        zip.Position = 0;

        var result = await CreateService(CreateDbContext()).ImportEmployeesAsync(zip);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Contain("zip archive");
    }

    [Fact]
    public async Task ImportEmployees_EmptyFile_ReturnsOneError()
    {
        var result = await CreateService(CreateDbContext()).ImportEmployeesAsync(new MemoryStream());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("empty");
    }

    // ── Header handling ──

    [Fact]
    public async Task ImportEmployees_MixedCaseHeaders_ImportSuccessfully()
    {
        // A file headed "FirstName"/"EMAIL" used to fail every row with "firstName and
        // lastName are required" — a message about the data, for a header problem.
        var csv = "AzureAdObjectId,FirstName,LastName,EMAIL,Title,OfficePhone,MobilePhone,OfficeLocation,DepartmentId\n"
                  + ",Jane,Smith,jane@test.com,Physician,+12025551234,,Floor 3,1";

        var db = CreateDbContext();
        var result = await CreateService(db).ImportEmployeesAsync(CsvToStream(csv));

        result.Errors.Should().BeEmpty();
        result.Imported.Should().Be(1);
        (await db.Employees.SingleAsync()).Email.Should().Be("jane@test.com");
    }

    [Fact]
    public async Task ImportEmployees_HeaderWithUtf8Bom_IsStillRecognised()
    {
        var csv = "﻿" + Headers + "\n,Jane,Smith,jane@test.com,Physician,+12025551234,,Floor 3,1";

        var result = await CreateService(CreateDbContext()).ImportEmployeesAsync(CsvToStream(csv));

        result.Errors.Should().BeEmpty();
        result.Imported.Should().Be(1);
    }

    [Fact]
    public async Task ImportEmployees_HumanReadableHeaders_MapOntoTheRightFields()
    {
        // The shape a real HR export actually arrives in: spaced, title-cased, with
        // synonyms and several columns this directory has no use for.
        var csv = "Employee ID,First Name,Last Name,Job Title,Department,Work Email,Work Phone,"
                  + "Work Location,Annual Salary" + Environment.NewLine
                  + "E-4471,Jane,Smith,Attending Physician,Cardiology,jane@test.com,(202) 555-0134,"
                  + "Floor 3 - West Wing,180000";

        var db = CreateDbContext();
        var result = await CreateService(db).ImportEmployeesAsync(CsvToStream(csv));

        result.Errors.Should().BeEmpty();
        result.Imported.Should().Be(1);

        var saved = await db.Employees.SingleAsync();
        saved.FirstName.Should().Be("Jane");
        saved.LastName.Should().Be("Smith");
        saved.Title.Should().Be("Attending Physician");
        saved.Email.Should().Be("jane@test.com");
        saved.OfficePhone.Should().Be("+12025550134"); // normalised from (202) 555-0134
        saved.OfficeLocation.Should().Be("Floor 3 - West Wing");

        // "Department" names a department rather than numbering it, so it is ignored
        // rather than failing the row — Salary and Employee ID likewise.
        saved.DepartmentId.Should().BeNull();
    }

    [Fact]
    public async Task ImportEmployees_BlankSynonymColumn_DoesNotOverwriteThePopulatedOne()
    {
        // Both columns canonicalise to email; the empty one must not win.
        var csv = "firstName,lastName,Email,Work Email" + Environment.NewLine
                  + "Jane,Smith,jane@test.com,";

        var db = CreateDbContext();
        var result = await CreateService(db).ImportEmployeesAsync(CsvToStream(csv));

        result.Errors.Should().BeEmpty();
        (await db.Employees.SingleAsync()).Email.Should().Be("jane@test.com");
    }

    // ── Error reporting ──

    [Fact]
    public async Task ImportEmployees_ManyBadRows_CapsTheListButNotTheCount()
    {
        // 40 rows all missing a name. The modal used to render one line each.
        var rows = string.Join("\n", Enumerable.Range(1, 40).Select(i => $",,,person{i}@test.com,,,,,"));
        var csv = Headers + "\n" + rows;

        var result = await CreateService(CreateDbContext()).ImportEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.TotalErrors.Should().Be(40);
        result.Errors.Should().HaveCount(26); // 25 listed + one summary line
        result.Errors.Last().Should().Contain("15 more");
    }

    [Fact]
    public async Task ImportEmployees_FewBadRows_ListsThemAll()
    {
        var csv = Headers + "\n,,,a@test.com,,,,,\n,,,b@test.com,,,,,";

        var result = await CreateService(CreateDbContext()).ImportEmployeesAsync(CsvToStream(csv));

        result.TotalErrors.Should().Be(2);
        result.Errors.Should().HaveCount(2);
    }

    // ── Shift import shares the same reader ──

    [Fact]
    public async Task ImportShifts_XlsxWithDateFormattedCells_ReadsThemAsTimes()
    {
        var db = CreateDbContext();
        var employeeId = Guid.NewGuid();
        db.Schedules.Add(new Schedule { Id = 1, Name = "ED Nights", DepartmentId = 1 });
        await db.SaveChangesAsync();

        // Excel stores a date as a serial number; only the cell's number format says it is
        // a date. Left raw, the shift parser rejects it as "startTime is required".
        var start = new DateTime(2026, 3, 2, 19, 0, 0);
        var end = new DateTime(2026, 3, 3, 7, 0, 0);
        List<string?[]> grid =
        [
            ["employeeId", "startTime", "endTime", "tier"],
            [employeeId.ToString(), null, null, "primary"],
        ];

        var stream = XlsxToStream(grid, dateCells: new Dictionary<(int Row, int Col), DateTime>
        {
            [(1, 1)] = start,
            [(1, 2)] = end,
        });

        var result = await CreateService(db).ImportShiftsAsync(1, stream);

        result.Errors.Should().BeEmpty();
        result.Imported.Should().Be(1);

        // The same two shifts written as ISO text in a CSV, for comparison.
        var csvDb = CreateDbContext();
        csvDb.Schedules.Add(new Schedule { Id = 1, Name = "ED Nights", DepartmentId = 1 });
        await csvDb.SaveChangesAsync();
        var csv = $"employeeId,startTime,endTime,tier{Environment.NewLine}"
                  + $"{employeeId},{start:yyyy-MM-ddTHH:mm:ss},{end:yyyy-MM-ddTHH:mm:ss},primary";
        (await CreateService(csvDb).ImportShiftsAsync(1, CsvToStream(csv))).Imported.Should().Be(1);

        var fromXlsx = await db.Shifts.SingleAsync();
        var fromCsv = await csvDb.Shifts.SingleAsync();
        fromXlsx.StartTime.Should().Be(fromCsv.StartTime);
        fromXlsx.EndTime.Should().Be(fromCsv.EndTime);
    }

    // ── Test helpers ──

    /// <summary>
    /// Builds a real .xlsx in memory. A null cell is written as absent, which is how Excel
    /// stores a blank — the behaviour the column-alignment test depends on.
    /// </summary>
    private static MemoryStream XlsxToStream(
        IEnumerable<string?[]> grid,
        bool useSharedStrings = false,
        Dictionary<(int Row, int Col), DateTime>? dateCells = null)
    {
        var stream = new MemoryStream();

        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();

            // Style 1 is a built-in date format (22 = "m/d/yy h:mm"); style 0 is General.
            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = new Stylesheet(new CellFormats(
                new CellFormat { NumberFormatId = 0 },
                new CellFormat { NumberFormatId = 22, ApplyNumberFormat = true }));
            stylesPart.Stylesheet.Save();

            var sharedStrings = new List<string>();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            var rowNumber = 0u;
            var gridRowIndex = 0;
            foreach (var cells in grid)
            {
                rowNumber++;
                var row = new Row { RowIndex = rowNumber };

                for (var column = 0; column < cells.Length; column++)
                {
                    var reference = $"{ColumnName(column)}{rowNumber}";

                    if (dateCells != null && dateCells.TryGetValue((gridRowIndex, column), out var date))
                    {
                        row.Append(new Cell
                        {
                            CellReference = reference,
                            StyleIndex = 1u,
                            CellValue = new CellValue(date.ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        });
                        continue;
                    }

                    var value = cells[column];
                    if (value == null) continue; // absent, exactly as Excel writes a blank
                    if (value.Length == 0) continue;

                    if (useSharedStrings)
                    {
                        var index = sharedStrings.IndexOf(value);
                        if (index < 0) { sharedStrings.Add(value); index = sharedStrings.Count - 1; }

                        row.Append(new Cell
                        {
                            CellReference = reference,
                            DataType = CellValues.SharedString,
                            CellValue = new CellValue(index.ToString()),
                        });
                    }
                    else
                    {
                        row.Append(new Cell
                        {
                            CellReference = reference,
                            DataType = CellValues.String,
                            CellValue = new CellValue(value),
                        });
                    }
                }

                sheetData.Append(row);
                gridRowIndex++;
            }

            if (useSharedStrings)
            {
                var stringPart = workbookPart.AddNewPart<SharedStringTablePart>();
                stringPart.SharedStringTable = new SharedStringTable(
                    sharedStrings.Select(s => new SharedStringItem(new Text(s))));
                stringPart.SharedStringTable.Save();
            }

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Staff",
            });

            workbookPart.Workbook.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static string ColumnName(int zeroBasedIndex)
    {
        var name = string.Empty;
        var index = zeroBasedIndex + 1;
        while (index > 0)
        {
            var remainder = (index - 1) % 26;
            name = (char)('A' + remainder) + name;
            index = (index - 1) / 26;
        }
        return name;
    }
}
