using System.Globalization;
using System.Text.RegularExpressions;
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
        CheckDuplicateEmails(records, errors);
        await ValidateDepartmentIdsAsync(records, errors);
        return new ImportResult { TotalRows = records.Count, Errors = errors, IsValid = errors.Count == 0 };
    }

    /// <summary>Imports employees from a CSV stream. Creates new, updates existing by AzureAdObjectId.</summary>
    public async Task<ImportResult> ImportEmployeesAsync(Stream csvStream, int? tenantId = null)
    {
        var (records, errors) = await ParseCsvAsync(csvStream, ParseEmployeeRow);
        if (errors.Count > 0)
            return new ImportResult { TotalRows = records.Count, Imported = 0, Errors = errors, IsValid = false };

        // Pre-import integrity checks
        CheckDuplicateEmails(records, errors);
        await ValidateDepartmentIdsAsync(records, errors);
        if (errors.Count > 0)
            return new ImportResult { TotalRows = records.Count, Imported = 0, Errors = errors, IsValid = false };

        var imported = 0;
        var rowIndex = 0;
        foreach (var row in records.OfType<EmployeeRow>())
        {
            rowIndex++;
            try
            {
                var existing = await FindExistingEmployeeAsync(row);

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
                    if (tenantId.HasValue) existing.TenantId = tenantId.Value;
                    existing.UpdatedAt = DateTime.UtcNow;
                    _logger.LogDebug("Imported employee {AzureAdObjectId}", row.AzureAdObjectId);
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
                        TenantId = tenantId,
                        Source = "Local",
                        LastSyncedAt = DateTime.UtcNow,
                    });
                    _logger.LogDebug("Created employee {EmployeeName}", row.AzureAdObjectId);
                }
                imported++;
            }
            catch (Exception ex)
            {
                var errorMsg = $"Row {rowIndex}: {ex.GetType().Name}: {ex.Message}";
                errors.Add(errorMsg);
                _logger.LogWarning(ex, "Error importing employee row {Row}: {Error}", rowIndex, ex.Message);
            }
        }

        if (errors.Count > 0)
        {
            _logger.LogWarning("Bulk import had {ErrorCount} error(s) — {Imported} row(s) buffered but not committed",
                errors.Count, imported);
            return new ImportResult { TotalRows = records.Count, Imported = 0, Errors = errors, IsValid = false };
        }

        if (errors.Count == 0)
        {
            try
            {
                await _db.SaveChangesAsync();
                _logger.LogInformation("Successfully saved {Imported} employees to database", imported);
            }
            catch (DbUpdateException ex)
            {
                var message = ex.InnerException?.Message ?? ex.Message;
                _logger.LogError(ex, "Database constraint violation during bulk import: {Message}", message);
                errors.Add($"Database error: {message}");
                return new ImportResult { TotalRows = records.Count, Imported = imported, Errors = errors, IsValid = false };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error saving to database: {Exception}", ex.ToString());
                errors.Add($"Database error: {ex.Message}");
                return new ImportResult { TotalRows = records.Count, Imported = imported, Errors = errors, IsValid = false };
            }
        }

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
            return new ImportResult { Errors = ["Schedule not found."], IsValid = false };

        var (records, errors) = await ParseCsvAsync(csvStream, row => ParseShiftRow(scheduleId, row));
        if (errors.Count > 0)
            return new ImportResult { TotalRows = records.Count, Imported = 0, Errors = errors, IsValid = false };

        var imported = 0;
        var rowIndex = 0;
        foreach (var row in records.OfType<ShiftRow>())
        {
            rowIndex++;
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
                errors.Add($"Row {rowIndex}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            _logger.LogWarning("Shift bulk import had {ErrorCount} error(s) — {Imported} row(s) buffered but not committed",
                errors.Count, imported);
            return new ImportResult { TotalRows = records.Count, Imported = 0, Errors = errors, IsValid = false };
        }

        try
        {
            await _db.SaveChangesAsync();
            _logger.LogInformation("Successfully saved {Imported} shifts to database for schedule {ScheduleId}", imported, scheduleId);
        }
        catch (DbUpdateException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            _logger.LogWarning(ex, "Database constraint violation during bulk shift import: {Message}", message);
            errors.Add($"Database error: {message}");
            return new ImportResult { TotalRows = records.Count, Imported = imported, Errors = errors, IsValid = false };
        }

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

    /// <summary>
    /// Parses a single CSV line, handling quoted fields and escaped double-quotes ("").
    /// </summary>
    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                // Handle escaped double-quotes ("""") inside quoted fields
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++; // skip the second quote of the pair
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (line[i] == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(line[i]);
            }
        }
        values.Add(current.ToString());

        return values.ToArray();
    }

    // ── Pre-import Validation Helpers ──

    /// <summary>Checks for duplicate emails within the CSV data and adds errors.</summary>
    private static void CheckDuplicateEmails(List<object> records, List<string> errors)
    {
        var duplicateEmails = records.OfType<EmployeeRow>()
            .GroupBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        foreach (var email in duplicateEmails)
            errors.Add($"Duplicate email found in CSV: '{email}' appears more than once.");
    }

    /// <summary>Validates that referenced department IDs exist in the database.</summary>
    private async Task ValidateDepartmentIdsAsync(List<object> records, List<string> errors)
    {
        var deptIds = records.OfType<EmployeeRow>()
            .Where(r => r.DepartmentId.HasValue)
            .Select(r => r.DepartmentId!.Value)
            .Distinct()
            .ToList();

        if (deptIds.Count == 0) return;

        var validIds = (await _db.Departments
            .Where(d => deptIds.Contains(d.Id))
            .Select(d => d.Id)
            .ToListAsync()).ToHashSet();

        foreach (var row in records.OfType<EmployeeRow>())
        {
            if (row.DepartmentId.HasValue && !validIds.Contains(row.DepartmentId.Value))
                errors.Add($"departmentId {row.DepartmentId} does not exist for '{row.Email}'.");
        }
    }

    /// <summary>
    /// Finds an existing employee by AzureAdObjectId. For synthetic IDs (no Azure AD link),
    /// falls back to deduplication by email to prevent duplicates on re-import.
    /// </summary>
    private async Task<Employee?> FindExistingEmployeeAsync(EmployeeRow row)
    {
        // First, try exact match by AzureAdObjectId
        var existing = await _db.Employees
            .FirstOrDefaultAsync(e => e.AzureAdObjectId == row.AzureAdObjectId);

        if (existing != null)
            return existing;

        // For synthetic IDs (no real Azure AD link), deduplicate by email
        if (row.AzureAdObjectId.StartsWith("csv-import-", StringComparison.Ordinal))
        {
            existing = await _db.Employees
                .FirstOrDefaultAsync(e => e.Email == row.Email);
            if (existing != null)
                _logger.LogDebug("Deduplicated by email for synthetic ID on '{Email}'", row.Email);
        }

        return existing;
    }

    // ── Row Parsers ──

    private static readonly Regex E164Regex = new(@"^\+[1-9]\d{1,14}$", RegexOptions.Compiled);

    private static (EmployeeRow? Record, string? Error) ParseEmployeeRow(Dictionary<string, string> row)
    {
        var azureAdId = row.GetValueOrDefault("azureAdObjectId", "")?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(azureAdId))
            azureAdId = $"csv-import-{Guid.NewGuid():N}";

        var firstName = row.GetValueOrDefault("firstName", "")?.Trim() ?? "";
        var lastName = row.GetValueOrDefault("lastName", "")?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
            return (null, "firstName and lastName are required.");

        var officePhone = row.GetValueOrDefault("officePhone")?.Trim();
        var mobilePhone = row.GetValueOrDefault("mobilePhone")?.Trim();

        // Validate phone numbers
        if (!string.IsNullOrWhiteSpace(officePhone) && !E164Regex.IsMatch(officePhone))
            return (null, $"officePhone '{officePhone}' is not valid E.164 format (+ followed by 2-15 digits, e.g. +1234567890).");

        if (!string.IsNullOrWhiteSpace(mobilePhone) && !E164Regex.IsMatch(mobilePhone))
            return (null, $"mobilePhone '{mobilePhone}' is not valid E.164 format (+ followed by 2-15 digits, e.g. +1234567890).");

        var email = row.GetValueOrDefault("email", "")?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(email))
            return (null, "email is required.");

        // Parse departmentId
        var deptIdStr = row.GetValueOrDefault("departmentId")?.Trim();
        int? departmentId = null;
        if (!string.IsNullOrWhiteSpace(deptIdStr))
        {
            if (int.TryParse(deptIdStr, out var did))
                departmentId = did;
            else
                return (null, $"departmentId '{deptIdStr}' is not a valid integer.");
        }

        return (new EmployeeRow
        {
            AzureAdObjectId = azureAdId,
            FirstName = firstName,
            LastName = lastName,
            Title = row.GetValueOrDefault("title")?.Trim(),
            Email = email,
            OfficePhone = string.IsNullOrWhiteSpace(officePhone) ? null : officePhone,
            MobilePhone = string.IsNullOrWhiteSpace(mobilePhone) ? null : mobilePhone,
            OfficeLocation = row.GetValueOrDefault("officeLocation")?.Trim(),
            DepartmentId = departmentId
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
