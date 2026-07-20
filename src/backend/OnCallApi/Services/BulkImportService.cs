using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Handles bulk CSV import of employees and schedule data.
/// Supports dry-run validation and transactional imports.
/// </summary>
public class BulkImportService
{
    private readonly AppDbContext _db;
    private readonly ILogger<BulkImportService> _logger;

    public BulkImportService(AppDbContext db, ILogger<BulkImportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Employee Import ──

    /// <summary>Validates a CSV of employee data without importing.</summary>
    public async Task<ImportResult> ValidateEmployeesAsync(Stream csvStream)
    {
        var (records, errors) = await ParseCsvAsync(csvStream, ParseEmployeeRow);
        return new ImportResult { TotalRows = records.Count, Errors = errors, IsValid = errors.Count == 0 };
    }

    /// <summary>Imports employees from a CSV stream. Creates new, updates existing by AzureAdObjectId.</summary>
    public async Task<ImportResult> ImportEmployeesAsync(Stream csvStream)
    {
        var (records, errors) = await ParseCsvAsync(csvStream, ParseEmployeeRow);
        if (errors.Count > 0)
            return new ImportResult { TotalRows = records.Count, Imported = 0, Errors = errors, IsValid = false };

        var imported = 0;
        foreach (var row in records.OfType<EmployeeRow>())
        {
            try
            {
                var existing = await _db.Employees
                    .FirstOrDefaultAsync(e => e.AzureAdObjectId == row.AzureAdObjectId);

                if (existing != null)
                {
                    existing.FirstName = row.FirstName;
                    existing.LastName = row.LastName;
                    existing.Title = row.Title;
                    existing.Email = row.Email;
                    existing.OfficePhone = row.OfficePhone;
                    existing.MobilePhone = row.MobilePhone;
                    existing.OfficeLocation = row.OfficeLocation;
                    existing.DepartmentId = row.DepartmentId;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    _db.Employees.Add(new Employee
                    {
                        AzureAdObjectId = row.AzureAdObjectId,
                        FirstName = row.FirstName,
                        LastName = row.LastName,
                        Title = row.Title,
                        Email = row.Email,
                        OfficePhone = row.OfficePhone,
                        MobilePhone = row.MobilePhone,
                        OfficeLocation = row.OfficeLocation,
                        DepartmentId = row.DepartmentId,
                        LastSyncedAt = DateTime.UtcNow,
                    });
                }
                imported++;
            }
            catch (Exception ex)
            {
                errors.Add($"Row {imported + 1}: {ex.Message}");
                _logger.LogWarning(ex, "Error importing employee row {Row}", imported + 1);
            }
        }

        if (errors.Count == 0)
            await _db.SaveChangesAsync();

        _logger.LogInformation("Bulk import completed: {Imported} employees processed with {Errors} errors", imported, errors.Count);
        return new ImportResult { TotalRows = records.Count, Imported = imported, Errors = errors, IsValid = errors.Count == 0 };
    }

    // ── Schedule / Shift Bulk Assign ──

    public async Task<ImportResult> ValidateScheduleImportAsync(int scheduleId, Stream csvStream)
    {
        var (records, errors) = await ParseCsvAsync(csvStream, row => ParseShiftRow(scheduleId, row));
        return new ImportResult { TotalRows = records.Count, Errors = errors, IsValid = errors.Count == 0 };
    }

    public async Task<ImportResult> ImportShiftsAsync(int scheduleId, Stream csvStream)
    {
        var schedule = await _db.Schedules.FindAsync(scheduleId);
        if (schedule == null)
            return new ImportResult { Errors = ["Schedule not found."] };

        var (records, errors) = await ParseCsvAsync(csvStream, row => ParseShiftRow(scheduleId, row));
        if (errors.Count > 0)
            return new ImportResult { TotalRows = records.Count, Imported = 0, Errors = errors, IsValid = false };

        var imported = 0;
        foreach (var row in records.OfType<ShiftRow>())
        {
            try
            {
                _db.Shifts.Add(new Shift
                {
                    ScheduleId = scheduleId,
                    EmployeeId = row.EmployeeId,
                    StartTime = row.StartTime,
                    EndTime = row.EndTime,
                    Tier = row.Tier,
                    Status = "scheduled"
                });
                imported++;
            }
            catch (Exception ex)
            {
                errors.Add($"Row {imported + 1}: {ex.Message}");
            }
        }

        if (errors.Count == 0)
            await _db.SaveChangesAsync();

        return new ImportResult { TotalRows = records.Count, Imported = imported, Errors = errors, IsValid = errors.Count == 0 };
    }

    // ── CSV Parsing ──

    private async Task<(List<object> Records, List<string> Errors)> ParseCsvAsync<T>(
        Stream csvStream, Func<Dictionary<string, string>, (T? Record, string? Error)> parser)
    {
        var records = new List<object>();
        var errors = new List<string>();

        using var reader = new StreamReader(csvStream);
        var headerLine = await reader.ReadLineAsync();
        if (headerLine == null)
        {
            errors.Add("CSV file is empty.");
            return (records, errors);
        }

        var headers = headerLine.Split(',').Select(h => h.Trim().Trim('"')).ToArray();
        var lineNumber = 1;

        while (!reader.EndOfStream)
        {
            lineNumber++;
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var values = ParseCsvLine(line);
                if (values.Length != headers.Length)
                {
                    errors.Add($"Row {lineNumber}: Expected {headers.Length} columns, got {values.Length}.");
                    continue;
                }

                var row = new Dictionary<string, string>();
                for (int i = 0; i < headers.Length; i++)
                    row[headers[i]] = values[i].Trim().Trim('"');

                var (record, error) = parser(row);
                if (error != null)
                    errors.Add($"Row {lineNumber}: {error}");
                else if (record != null)
                    records.Add(record);
            }
            catch (Exception ex)
            {
                errors.Add($"Row {lineNumber}: {ex.Message}");
            }
        }

        return (records, errors);
    }

    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                inQuotes = !inQuotes;
            else if (line[i] == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(line[i]);
        }
        values.Add(current.ToString());

        return values.ToArray();
    }

    // ── Row Parsers ──

    private static (EmployeeRow? Record, string? Error) ParseEmployeeRow(Dictionary<string, string> row)
    {
        var azureAdId = row.GetValueOrDefault("azureAdObjectId", "");
        if (string.IsNullOrWhiteSpace(azureAdId))
            return (null, "azureAdObjectId is required.");

        var firstName = row.GetValueOrDefault("firstName", "");
        var lastName = row.GetValueOrDefault("lastName", "");
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
            return (null, "firstName and lastName are required.");

        return (new EmployeeRow
        {
            AzureAdObjectId = azureAdId,
            FirstName = firstName,
            LastName = lastName,
            Title = row.GetValueOrDefault("title"),
            Email = row.GetValueOrDefault("email", ""),
            OfficePhone = row.GetValueOrDefault("officePhone"),
            MobilePhone = row.GetValueOrDefault("mobilePhone"),
            OfficeLocation = row.GetValueOrDefault("officeLocation"),
            DepartmentId = int.TryParse(row.GetValueOrDefault("departmentId"), out var did) ? did : null
        }, null);
    }

    private (ShiftRow? Record, string? Error) ParseShiftRow(int scheduleId, Dictionary<string, string> row)
    {
        if (!Guid.TryParse(row.GetValueOrDefault("employeeId"), out var empId))
            return (null, "employeeId must be a valid GUID.");

        if (!DateTime.TryParse(row.GetValueOrDefault("startTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start))
            return (null, "startTime is required (ISO 8601 format).");

        if (!DateTime.TryParse(row.GetValueOrDefault("endTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end))
            return (null, "endTime is required (ISO 8601 format).");

        if (end <= start)
            return (null, "endTime must be after startTime.");

        var tier = row.GetValueOrDefault("tier", "primary");
        if (tier is not ("primary" or "secondary" or "tertiary"))
            return (null, "tier must be primary, secondary, or tertiary.");

        return (new ShiftRow
        {
            EmployeeId = empId,
            StartTime = start,
            EndTime = end,
            Tier = tier
        }, null);
    }

    // ── Internal Types ──

    private record EmployeeRow
    {
        public string AzureAdObjectId { get; init; } = string.Empty;
        public string FirstName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public string? Title { get; init; }
        public string Email { get; init; } = string.Empty;
        public string? OfficePhone { get; init; }
        public string? MobilePhone { get; init; }
        public string? OfficeLocation { get; init; }
        public int? DepartmentId { get; init; }
    }

    private record ShiftRow
    {
        public Guid EmployeeId { get; init; }
        public DateTime StartTime { get; init; }
        public DateTime EndTime { get; init; }
        public string Tier { get; init; } = "primary";
    }
}

public class ImportResult
{
    public int TotalRows { get; set; }
    public int Imported { get; set; }
    public List<string> Errors { get; set; } = [];
    public bool IsValid { get; set; } = true;
}
