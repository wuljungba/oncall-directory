using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace BackendTests.Services;

public class BulkImportServiceTests
{
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

    private static BulkImportService CreateService(AppDbContext db)
    {
        return new BulkImportService(db, NullLogger<BulkImportService>.Instance);
    }

    private static MemoryStream CsvToStream(string csv)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(csv);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    // ── Validation Tests ──

    [Fact]
    public async Task ValidateEmployees_ValidCsv_ReturnsValid()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,title,officePhone,mobilePhone,officeLocation,departmentId\n" +
                   "obj-1,Jane,Smith,jane@test.com,Physician,+12025551234,,Floor 3,1";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.TotalRows.Should().Be(1);
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateEmployees_EmptyFile_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var result = await service.ValidateEmployeesAsync(CsvToStream(""));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("empty"));
    }

    [Fact]
    public async Task ValidateEmployees_HeaderOnly_ReturnsValid()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,departmentId";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.TotalRows.Should().Be(0);
    }

    [Fact]
    public async Task ValidateEmployees_MissingEmail_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email\n" +
                   "obj-1,Jane,Smith,";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("email is required"));
    }

    [Fact]
    public async Task ValidateEmployees_MissingFirstAndLastName_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email\n" +
                   "obj-1,,,jane@test.com";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("firstName and lastName"));
    }

    [Fact]
    public async Task ValidateEmployees_InvalidPhone_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,officePhone\n" +
                   "obj-1,Jane,Smith,jane@test.com,555-1234";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("officePhone"));
    }

    [Fact]
    public async Task ValidateEmployees_InvalidDepartmentIdFormat_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,departmentId\n" +
                   "obj-1,Jane,Smith,jane@test.com,not-a-number";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("departmentId"));
    }

    [Fact]
    public async Task ValidateEmployees_DuplicateEmail_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,departmentId\n" +
                   "obj-1,Jane,Smith,jane@test.com,1\n" +
                   "obj-2,Jane,Doe,jane@test.com,1";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Duplicate email"));
    }

    [Fact]
    public async Task ValidateEmployees_NonExistentDepartment_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,departmentId\n" +
                   "obj-1,Jane,Smith,jane@test.com,999";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("departmentId 999"));
    }

    [Fact]
    public async Task ValidateEmployees_AutoGeneratesSyntheticAzureAdId()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,departmentId\n" +
                   ",Jane,Smith,jane@test.com,1";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        // Should pass validation despite missing azureAdObjectId
        result.IsValid.Should().BeTrue();
        result.TotalRows.Should().Be(1);
    }

    // ── Employee Import Tests ──

    [Fact]
    public async Task ImportEmployees_ValidCsv_CreatesEmployees()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,title,departmentId\n" +
                   "obj-1,Jane,Smith,jane@test.com,Physician,1";
        var result = await service.ImportEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.Imported.Should().Be(1);

        var saved = await db.Employees.FirstOrDefaultAsync(e => e.AzureAdObjectId == "obj-1");
        saved.Should().NotBeNull();
        saved!.FirstName.Should().Be("Jane");
        saved.Email.Should().Be("jane@test.com");
        saved.DepartmentId.Should().Be(1);
    }

    [Fact]
    public async Task ImportEmployees_UpdatesExistingByAzureAdId()
    {
        var db = CreateDbContext();
        var existing = new Employee
        {
            AzureAdObjectId = "obj-1",
            FirstName = "Old",
            LastName = "Name",
            Email = "old@test.com",
        };
        db.Employees.Add(existing);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,title,departmentId\n" +
                   "obj-1,Jane,Smith,jane@test.com,Physician,1";
        var result = await service.ImportEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.Imported.Should().Be(1);

        var updated = await db.Employees.FirstAsync(e => e.AzureAdObjectId == "obj-1");
        updated.FirstName.Should().Be("Jane");
        updated.Email.Should().Be("jane@test.com");
        updated.DepartmentId.Should().Be(1);
    }

    [Fact]
    public async Task ImportEmployees_SyntheticId_DeduplicatesByEmail()
    {
        var db = CreateDbContext();
        var existing = new Employee
        {
            AzureAdObjectId = "csv-import-old-guid",
            Email = "jane@test.com",
            FirstName = "Jane",
            LastName = "Smith",
        };
        db.Employees.Add(existing);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Re-import with blank azureAdObjectId (will get new synthetic ID) but same email
        var csv = "azureAdObjectId,firstName,lastName,email,departmentId\n" +
                   ",Jane,Smith,jane@test.com,1";
        var result = await service.ImportEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.Imported.Should().Be(1);

        // Should have updated the existing employee, not created a duplicate
        var count = await db.Employees.CountAsync(e => e.Email == "jane@test.com");
        count.Should().Be(1);

        // Non-synthetic ID should NOT dedup by email
        var existingByObjId = await db.Employees.FirstAsync(e => e.AzureAdObjectId == "csv-import-old-guid");
        existingByObjId.Should().NotBeNull();
    }

    [Fact]
    public async Task ImportEmployees_DuplicateEmailInCsv_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,departmentId\n" +
                   "obj-1,Jane,Smith,jane@test.com,1\n" +
                   "obj-2,Jane,Doe,jane@test.com,1";
        var result = await service.ImportEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Duplicate email"));
        result.Imported.Should().Be(0);
    }

    [Fact]
    public async Task ImportEmployees_NonExistentDepartment_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,departmentId\n" +
                   "obj-1,Jane,Smith,jane@test.com,999";
        var result = await service.ImportEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("departmentId 999"));
        result.Imported.Should().Be(0);
    }

    [Fact]
    public async Task ImportEmployees_RealAzureAdId_DoesNotDedupByEmail()
    {
        var db = CreateDbContext();
        var existing = new Employee
        {
            AzureAdObjectId = "real-ad-id",
            Email = "jane@test.com",
            FirstName = "Jane",
            LastName = "Smith",
        };
        db.Employees.Add(existing);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Different AzureAdObjectId, same email — should create a NEW employee
        // because the ID is not synthetic ("csv-import-*" prefix)
        var csv = "azureAdObjectId,firstName,lastName,email,departmentId\n" +
                   "different-real-id,Jane,Smith,jane@test.com,1";
        var result = await service.ImportEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.Imported.Should().Be(1);

        var count = await db.Employees.CountAsync(e => e.Email == "jane@test.com");
        count.Should().Be(2);
    }

    [Fact]
    public async Task ImportEmployees_ParseErrorOnRow_AbortsImport()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        // Row 1 has a wrong column count (parse error), Row 2 is valid
        var csv = "azureAdObjectId,firstName,lastName,email,departmentId\n" +
                   "obj-1,Alice,Smith,alice@test.com,1,extra-garbage\n" +
                   "obj-2,Bob,Jones,bob@test.com,1";
        var result = await service.ImportEmployeesAsync(CsvToStream(csv));

        // Import is aborted due to parse error — no rows imported
        result.Imported.Should().Be(0);
        result.Errors.Should().Contain(e => e.Contains("Expected 5 columns"));
        result.IsValid.Should().BeFalse();
    }

    // ── Shift Validation Tests ──

    [Fact]
    public async Task ValidateShifts_ValidCsv_ReturnsValid()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "employeeId,startTime,endTime,tier\n" +
                   "11111111-1111-1111-1111-111111111111,2026-01-15T08:00:00Z,2026-01-15T20:00:00Z,primary";
        var result = await service.ValidateScheduleImportAsync(1, CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.TotalRows.Should().Be(1);
    }

    [Fact]
    public async Task ValidateShifts_InvalidTier_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "employeeId,startTime,endTime,tier\n" +
                   "11111111-1111-1111-1111-111111111111,2026-01-15T08:00:00Z,2026-01-15T20:00:00Z,invalid";
        var result = await service.ValidateScheduleImportAsync(1, CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("tier"));
    }

    [Fact]
    public async Task ValidateShifts_InvalidEmployeeId_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "employeeId,startTime,endTime,tier\n" +
                   "not-a-guid,2026-01-15T08:00:00Z,2026-01-15T20:00:00Z,primary";
        var result = await service.ValidateScheduleImportAsync(1, CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("employeeId"));
    }

    [Fact]
    public async Task ValidateShifts_EndBeforeStart_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "employeeId,startTime,endTime,tier\n" +
                   "11111111-1111-1111-1111-111111111111,2026-01-15T20:00:00Z,2026-01-15T08:00:00Z,primary";
        var result = await service.ValidateScheduleImportAsync(1, CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("endTime must be after"));
    }

    // ── Shift Import Tests ──

    [Fact]
    public async Task ImportShifts_ScheduleNotFound_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "employeeId,startTime,endTime,tier\n" +
                   "11111111-1111-1111-1111-111111111111,2026-01-15T08:00:00Z,2026-01-15T20:00:00Z,primary";
        var result = await service.ImportShiftsAsync(999, CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Schedule not found"));
    }

    [Fact]
    public async Task ImportShifts_ValidCsv_CreatesShifts()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        // Create a schedule for the shift
        db.Schedules.Add(new Schedule
        {
            Id = 1,
            Name = "Test Schedule",
            DepartmentId = 1,
            RotationType = "weekly",
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddMonths(1),
        });
        await db.SaveChangesAsync();

        var csv = "employeeId,startTime,endTime,tier\n" +
                   "11111111-1111-1111-1111-111111111111,2026-01-15T08:00:00Z,2026-01-15T20:00:00Z,primary\n" +
                   "22222222-2222-2222-2222-222222222222,2026-01-16T08:00:00Z,2026-01-16T20:00:00Z,secondary";
        var result = await service.ImportShiftsAsync(1, CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.Imported.Should().Be(2);

        var shifts = await db.Shifts.Where(s => s.ScheduleId == 1).ToListAsync();
        shifts.Should().HaveCount(2);
        shifts.Should().Contain(s => s.Tier == "primary");
        shifts.Should().Contain(s => s.Tier == "secondary");
    }

    // ── CSV Parsing Edge Cases ──

    [Fact]
    public async Task ParseCsv_HandlesQuotedFieldsWithCommas()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,officeLocation\n" +
                   "obj-1,Jane,Smith,jane@test.com,\"Floor 3, Room 310\"";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.TotalRows.Should().Be(1);
    }

    [Fact]
    public async Task ParseCsv_HandlesEscapedQuotesInField()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,title\n" +
                   "obj-1,Jane,Smith,jane@test.com,\"RN, BSN \"\"oncology\"\"\"";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.TotalRows.Should().Be(1);
    }

    [Fact]
    public async Task ParseCsv_SkipsEmptyLines()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email\n" +
                   "obj-1,Jane,Smith,jane@test.com\n" +
                   "\n" +
                   "obj-2,Bob,Jones,bob@test.com";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.TotalRows.Should().Be(2);
    }

    [Fact]
    public async Task ParseCsv_WrongColumnCount_ReturnsError()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email\n" +
                   "obj-1,Jane,Smith,jane@test.com,extra-column";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Expected 4 columns, got 5"));
    }

    [Fact]
    public async Task ParseCsv_TrimsWhitespace()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email\n" +
                   "  obj-1  ,  Jane  ,  Smith  ,  jane@test.com  ";
        var result = await service.ValidateEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.TotalRows.Should().Be(1);
    }

    // ── Multiple Row Tests ──

    [Fact]
    public async Task ImportEmployees_MultipleRows_AllValid()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email,departmentId\n" +
                   "obj-1,Alice,Smith,alice@test.com,1\n" +
                   "obj-2,Bob,Jones,bob@test.com,1\n" +
                   "obj-3,Carol,Williams,carol@test.com,2";
        var result = await service.ImportEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.Imported.Should().Be(3);

        var count = await db.Employees.CountAsync();
        count.Should().Be(3);
    }

    [Fact]
    public async Task ImportEmployees_DepartmentIdNull_DoesNotValidate()
    {
        var db = CreateDbContext();
        var service = CreateService(db);

        var csv = "azureAdObjectId,firstName,lastName,email\n" +
                   "obj-1,Jane,Smith,jane@test.com";
        var result = await service.ImportEmployeesAsync(CsvToStream(csv));

        result.IsValid.Should().BeTrue();
        result.Imported.Should().Be(1);
    }
}
