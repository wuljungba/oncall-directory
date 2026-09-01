using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services.Import;
using OnCallApi.Validators;

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
    public async Task<ImportResult> ValidateEmployeesAsync(
        Stream csvStream, int? tenantId = null, List<int>? allowedTenantIds = null)
    {
        var (records, errors) = await ParseCsvAsync(csvStream, ParseEmployeeRow);
        CheckDuplicateEmails(records, errors);
        await ResolveDepartmentNamesAsync(records, tenantId, allowedTenantIds, errors);
        await ValidateDepartmentIdsAsync(records, errors);
        return Result(records.Count, 0, errors);
    }

    /// <summary>
    /// Imports employees from a CSV stream. Creates new, updates existing by AzureAdObjectId.
    /// </summary>
    /// <param name="tenantId">The tenant new records are created in.</param>
    /// <param name="allowedTenantIds">
    /// The tenants the caller may modify, or null for a super admin (unrestricted).
    /// An existing employee outside this set is never matched, so an import can neither
    /// overwrite nor take over another customer's record.
    /// </param>
    public async Task<ImportResult> ImportEmployeesAsync(
        Stream csvStream, int? tenantId = null, List<int>? allowedTenantIds = null)
    {
        var (records, errors) = await ParseCsvAsync(csvStream, ParseEmployeeRow);
        if (errors.Count > 0)
            return Result(records.Count, 0, errors);

        // Pre-import integrity checks
        CheckDuplicateEmails(records, errors);
        await ResolveDepartmentNamesAsync(records, tenantId, allowedTenantIds, errors);
        await ValidateDepartmentIdsAsync(records, errors);
        await CheckForeignTenantEmailsAsync(records, allowedTenantIds, errors);
        if (errors.Count > 0)
            return Result(records.Count, 0, errors);

        // Resolved once for the whole file: a per-row lookup would issue one query per
        // contact for a value that cannot change mid-import.
        var extensionPrefix = await DialPlan.ResolveExtensionPrefixAsync(_db, tenantId);

        var imported = 0;
        var rowIndex = 0;
        foreach (var row in records.OfType<EmployeeRow>())
        {
            rowIndex++;
            try
            {
                var existing = await FindExistingEmployeeAsync(row, allowedTenantIds);

                if (existing != null)
                {
                    // Only columns the file actually carried are written. Absent columns
                    // are left alone rather than nulled -- see EmployeeRow.PresentColumns.
                    existing.FirstName = row.FirstName;
                    existing.LastName = row.LastName;
                    // Guarded like every other optional column: now that email can be
                    // absent, an unguarded write let a narrower follow-up file erase the
                    // address of everyone it listed while reporting complete success.
                    if (row.PresentColumns.Contains("email")) existing.Email = row.Email;
                    if (row.PresentColumns.Contains("contactType")) existing.ContactType = row.ContactType;
                    if (row.PresentColumns.Contains("displayName")) existing.DisplayName = row.DisplayName;
                    if (row.PresentColumns.Contains("extension")) existing.Extension = row.Extension;
                    if (row.PresentColumns.Contains("credentials") || row.PresentColumns.Contains("name"))
                        existing.Credentials = row.Credentials;
                    if (row.PresentColumns.Contains("title")) existing.Title = row.Title;
                    if (row.PresentColumns.Contains("officePhone")) existing.OfficePhone = row.OfficePhone;
                    if (row.PresentColumns.Contains("mobilePhone")) existing.MobilePhone = row.MobilePhone;
                    if (row.PresentColumns.Contains("officeLocation")) existing.OfficeLocation = row.OfficeLocation;
                    if (row.PresentColumns.Contains("departmentId") || row.PresentColumns.Contains("department"))
                        existing.DepartmentId = row.DepartmentId;
                    DialPlan.ApplyExtensionPrefix(existing, extensionPrefix);
                    // TenantId is deliberately NOT reassigned. It used to be set to the
                    // importer's tenant, which meant uploading a file containing another
                    // customer's employee email silently moved that person into your
                    // directory and overwrote their name, title and phone numbers.
                    existing.UpdatedAt = DateTime.UtcNow;
                    _logger.LogDebug("Imported employee {AzureAdObjectId}", row.AzureAdObjectId);
                }
                else
                {
                    var created = new Employee
                    {
                        AzureAdObjectId = row.AzureAdObjectId,
                        FirstName = row.FirstName,
                        LastName = row.LastName,
                        ContactType = row.ContactType,
                        DisplayName = row.DisplayName,
                        Extension = row.Extension,
                        Credentials = row.Credentials,
                        Title = row.Title,
                        Email = row.Email,
                        OfficePhone = row.OfficePhone,
                        MobilePhone = row.MobilePhone,
                        OfficeLocation = row.OfficeLocation,
                        DepartmentId = row.DepartmentId,
                        TenantId = tenantId,
                        Source = "CsvImport",
                        LastSyncedAt = DateTime.UtcNow,
                    };

                    // A row that gave only "x3434" gets a dialable number when the
                    // subscription has a dial plan, and keeps just the extension when it
                    // does not. See DialPlan for why nothing is invented.
                    DialPlan.ApplyExtensionPrefix(created, extensionPrefix);

                    _db.Employees.Add(created);
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
            return Result(records.Count, 0, errors);
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
                return Result(records.Count, imported, errors);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error saving to database: {Exception}", ex.ToString());
                errors.Add($"Database error: {ex.Message}");
                return Result(records.Count, imported, errors);
            }
        }

        _logger.LogInformation("Bulk import completed: {Imported} employees processed with {Errors} errors", imported, errors.Count);
        return Result(records.Count, imported, errors);
    }

    // ── Schedule / Shift Bulk Assign ──

    public async Task<ImportResult> ValidateScheduleImportAsync(int scheduleId, Stream csvStream)
    {
        var (records, errors) = await ParseCsvAsync(csvStream, row => ParseShiftRow(scheduleId, row));
        return Result(records.Count, 0, errors);
    }

    public async Task<ImportResult> ImportShiftsAsync(int scheduleId, Stream csvStream)
    {
        var schedule = await _db.Schedules.FindAsync(scheduleId);
        if (schedule == null)
            return Result(0, 0, ["Schedule not found."]);

        var (records, errors) = await ParseCsvAsync(csvStream, row => ParseShiftRow(scheduleId, row));
        if (errors.Count > 0)
            return Result(records.Count, 0, errors);

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
            return Result(records.Count, 0, errors);
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
            return Result(records.Count, imported, errors);
        }

        return Result(records.Count, imported, errors);
    }

    // ── CSV Parsing ──

    /// <summary>
    /// Reads an uploaded file into records, whichever tabular format it arrived in.
    ///
    /// Reading and format detection belong to <see cref="TabularFileReader"/>; what stays
    /// here is what the rows mean — column widths, per-row validation, and the messages
    /// the person importing sees.
    /// </summary>
    private static async Task<(List<object> Records, List<string> Errors)> ParseCsvAsync<T>(
        Stream csvStream, Func<Dictionary<string, string>, (T? Record, string? Error)> parser)
    {
        var records = new List<object>();
        var errors = new List<string>();

        var (document, readError) = await TabularFileReader.ReadAsync(csvStream);
        if (readError != null || document == null)
        {
            errors.Add(readError ?? "The uploaded file could not be read.");
            return (records, errors);
        }

        var headers = document.Headers.Select(CanonicalHeader).ToArray();

        foreach (var row in document.Rows)
        {
            try
            {
                if (row.Values.Length != headers.Length)
                {
                    errors.Add($"Row {row.Number}: Expected {headers.Length} columns, got {row.Values.Length}.");
                    continue;
                }

                var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Length; i++)
                {
                    var value = row.Values[i].Trim().Trim('"');

                    // Two columns can canonicalise to the same field ("Email" alongside
                    // "Work Email"). Never let a blank one overwrite a populated one.
                    if (value.Length == 0 && cells.ContainsKey(headers[i])) continue;
                    cells[headers[i]] = value;
                }

                var (record, error) = parser(cells);
                if (error != null)
                    errors.Add($"Row {row.Number}: {error}");
                else if (record != null)
                    records.Add(record);
            }
            catch (Exception ex)
            {
                errors.Add($"Row {row.Number}: {ex.Message}");
            }
        }

        return (records, errors);
    }

    /// <summary>
    /// Column names a real staff export uses for the fields this importer wants.
    ///
    /// Keys are compared after spaces, underscores and hyphens are stripped and casing is
    /// ignored, so "First Name", "first_name" and "FIRSTNAME" all reach firstName without
    /// an entry here. Only genuine synonyms are listed.
    ///
    /// "Department" carries a name, not an id -- every real export writes "Cardiology"
    /// rather than 7. It used to be ignored, so staff imported cleanly and arrived with no
    /// department at all, which left them invisible to every department-scoped on-call
    /// lookup. The name is now resolved against the departments the importer may use.
    /// </summary>
    private static readonly Dictionary<string, string> HeaderAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["departmentname"] = "department",
        ["dept"] = "department",
        ["givenname"] = "firstName",
        ["forename"] = "firstName",
        ["surname"] = "lastName",
        ["familyname"] = "lastName",

        // One combined name column, which is what a real staff export usually has.
        ["name"] = "name",
        ["fullname"] = "name",
        ["employeename"] = "name",
        ["staffname"] = "name",
        ["provider"] = "name",
        ["providername"] = "name",
        ["physician"] = "name",
        ["clinician"] = "name",

        ["credentials"] = "credentials",
        ["degree"] = "credentials",
        ["degrees"] = "credentials",
        ["postnominals"] = "credentials",

        ["workemail"] = "email",
        ["emailaddress"] = "email",
        ["primaryemail"] = "email",

        ["jobtitle"] = "title",
        ["position"] = "title",
        ["role"] = "title",

        ["workphone"] = "officePhone",
        ["deskphone"] = "officePhone",
        ["desknumber"] = "officePhone",
        ["officenumber"] = "officePhone",
        // An unqualified "Phone" in a staff directory is the desk line. If a file means a
        // mobile by it, the number still lands in the directory, just in the office field.
        ["phone"] = "officePhone",
        ["phonenumber"] = "officePhone",

        ["mobile"] = "mobilePhone",
        ["mobilenumber"] = "mobilePhone",
        ["cell"] = "mobilePhone",
        ["cellphone"] = "mobilePhone",

        ["worklocation"] = "officeLocation",
        ["location"] = "officeLocation",
        ["office"] = "officeLocation",

        // A unit/service-line row ("3North") carries a label rather than a person's name.
        ["displayname"] = "displayName",
        ["contactname"] = "displayName",
        ["unit"] = "displayName",
        ["ward"] = "displayName",
        ["floor"] = "displayName",
        ["label"] = "displayName",

        ["ext"] = "extension",
        ["ext."] = "extension",
        ["extn"] = "extension",
        ["x"] = "extension",
        ["deskextension"] = "extension",
        ["phoneextension"] = "extension",

        ["type"] = "contactType",
        ["recordtype"] = "contactType",
    };

    /// <summary>
    /// Maps one file column onto the field it means.
    ///
    /// A hospital HR export is headed "First Name" and "Work Email", not "firstName" and
    /// "email". Matching those literally failed every row with "firstName and lastName are
    /// required" — a message about the data, for a header problem.
    /// </summary>
    private static string CanonicalHeader(string header)
    {
        var compact = new string(header
            .Where(c => !char.IsWhiteSpace(c) && c != '_' && c != '-')
            .ToArray());

        return HeaderAliases.TryGetValue(compact, out var canonical) ? canonical : compact;
    }

    /// <summary>
    /// Caps the error list so a wholly unsuitable file reports a legible problem rather
    /// than one line per row, while <see cref="ImportResult.TotalErrors"/> still reports
    /// how many there really were.
    /// </summary>
    private const int MaxReportedErrors = 25;

    private static ImportResult Result(int totalRows, int imported, List<string> errors)
    {
        var reported = errors.Count > MaxReportedErrors
            ? errors.Take(MaxReportedErrors)
                .Append($"... and {errors.Count - MaxReportedErrors} more error(s) not shown.")
                .ToList()
            : errors;

        return new ImportResult
        {
            TotalRows = totalRows,
            Imported = imported,
            Errors = reported,
            TotalErrors = errors.Count,
            IsValid = errors.Count == 0,
        };
    }

    // ── Pre-import Validation Helpers ──

    /// <summary>Checks for duplicate emails within the CSV data and adds errors.</summary>
    private static void CheckDuplicateEmails(List<object> records, List<string> errors)
    {
        // Rows with no email are excluded rather than grouped: several department
        // contacts legitimately have none, and grouping them together would report a
        // dozen units as "the same duplicate address" and fail the whole import.
        var duplicateEmails = records.OfType<EmployeeRow>()
            .Where(r => !string.IsNullOrWhiteSpace(r.Email))
            .GroupBy(r => r.Email!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        foreach (var email in duplicateEmails)
            errors.Add($"Duplicate email found in CSV: '{email}' appears more than once.");
    }

    /// <summary>
    /// Resolves the department column's names to ids, within the departments this import
    /// may file against.
    ///
    /// Unknown names fail the import rather than importing the person without a
    /// department: a typo would otherwise put someone in the directory but outside every
    /// department-scoped on-call lookup, which reads as "missing" long after the import
    /// reported success. Names are never created -- departments drive on-call routing, and
    /// a misspelling would quietly split one department into two.
    /// </summary>
    private async Task ResolveDepartmentNamesAsync(
        List<object> records, int? tenantId, List<int>? allowedTenantIds, List<string> errors)
    {
        var named = records.OfType<EmployeeRow>()
            .Where(r => !string.IsNullOrWhiteSpace(r.DepartmentName))
            .ToList();
        if (named.Count == 0) return;

        var query = _db.Departments.AsQueryable();
        if (tenantId.HasValue)
            query = query.Where(d => d.TenantId == tenantId.Value);
        else if (allowedTenantIds is { Count: > 0 })
            query = query.Where(d => d.TenantId.HasValue && allowedTenantIds.Contains(d.TenantId.Value));

        var departments = await query.Select(d => new { d.Id, d.Name }).ToListAsync();

        // Compared the way people type them: casing and spacing vary between exports.
        static string Key(string name) =>
            new string(name.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

        var byName = new Dictionary<string, int>();
        foreach (var d in departments)
            byName[Key(d.Name)] = d.Id;

        var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in named)
        {
            if (byName.TryGetValue(Key(row.DepartmentName!), out var id))
                row.DepartmentId = id;
            else
                unknown.Add(row.DepartmentName!);
        }

        if (unknown.Count == 0) return;

        var available = departments.Count == 0
            ? "There are no departments to import into yet — create them first."
            : "Available: " + string.Join(", ", departments.OrderBy(d => d.Name).Select(d => d.Name));

        errors.Add(
            $"These department names were not recognised: {string.Join(", ", unknown)}. {available}");
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
                errors.Add($"departmentId {row.DepartmentId} does not exist for '{Describe(row)}'.");
        }
    }

    /// <summary>
    /// Finds an existing employee by AzureAdObjectId. For synthetic IDs (no Azure AD link),
    /// falls back to deduplication by email to prevent duplicates on re-import.
    /// </summary>
    /// <summary>
    /// Reports rows whose email already belongs to an employee outside the caller's tenants.
    ///
    /// Employee.Email is unique across the WHOLE table, not per tenant. Now that the lookup
    /// is tenant-scoped, such a row no longer matches and would fall through to an insert
    /// that violates that index -- surfacing as a DbUpdateException rather than something a
    /// user can act on. Catching it here turns it into a plain message.
    ///
    /// The message deliberately says only that the address is already in use elsewhere. It
    /// names no employee, tenant or field value.
    /// </summary>
    private async Task CheckForeignTenantEmailsAsync(
        List<object> records, List<int>? allowedTenantIds, List<string> errors)
    {
        // A super admin is unrestricted, so nothing is foreign to them.
        if (allowedTenantIds == null) return;

        var emails = records.OfType<EmployeeRow>()
            .Select(r => r.Email)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (emails.Count == 0) return;

        // A record with no tenant is UNCLAIMED, not another organization's. Treating null
        // as foreign made every employee created by the app's own primary onboarding path
        // (which leaves TenantId null) permanently un-importable by a scoped admin, with a
        // message telling them it belonged to someone else.
        var foreign = await _db.Employees
            .Where(e => e.Email != null && emails.Contains(e.Email))
            .Where(e => e.TenantId.HasValue && !allowedTenantIds.Contains(e.TenantId.Value))
            .Select(e => e.Email)
            .ToListAsync();

        foreach (var email in foreign.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Email '{email}' already belongs to a directory entry in another organization "
                + "and cannot be imported here. Ask a super admin to move or release it.");
        }
    }

    private async Task<Employee?> FindExistingEmployeeAsync(EmployeeRow row, List<int>? allowedTenantIds)
    {
        // Both lookups are restricted to the tenants the caller may modify. Without this
        // the match ignored tenancy entirely, so a scoped admin could resolve — and then
        // overwrite — an employee belonging to a different customer.
        var candidates = _db.Employees.AsQueryable();
        if (allowedTenantIds != null)
        {
            candidates = candidates.Where(e =>
                e.TenantId.HasValue && allowedTenantIds.Contains(e.TenantId.Value));
        }

        // First, try exact match by AzureAdObjectId
        var existing = await candidates
            .FirstOrDefaultAsync(e => e.AzureAdObjectId == row.AzureAdObjectId);

        if (existing != null)
            return existing;

        // For synthetic IDs (no real Azure AD link), deduplicate by email -- but only
        // when the row HAS one. Matching on a null address would let the first
        // email-less department contact swallow every later one, so re-importing a
        // sheet of twelve units would repeatedly overwrite a single row.
        if (row.AzureAdObjectId.StartsWith("csv-import-", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(row.Email))
        {
            // Compared case-insensitively on purpose: SQLite matches case-sensitively by
            // default, so JANE@x.org and jane@x.org became two records for one clinician,
            // while SQL Server's collation would instead reject the insert outright.
            var email = row.Email!.ToLowerInvariant();
            existing = await candidates
                .FirstOrDefaultAsync(e => e.Email != null && e.Email.ToLower() == email);
            if (existing != null)
                _logger.LogDebug("Deduplicated by email for synthetic ID on '{Email}'", row.Email);
        }

        return existing;
    }

    // ── Row Parsers ──

    private static readonly Regex E164Regex = new(@"^\+[1-9]\d{1,14}$", RegexOptions.Compiled);

    /// <summary>
    /// Normalizes a phone number from a CSV cell to E.164, or explains why it cannot be.
    /// Blank stays blank. Anything genuinely unusable — letters, an extension, a truncated
    /// number — is still rejected, because a number that cannot be dialled is worse in the
    /// directory than an empty field.
    /// </summary>
    private static (string? Value, string? Error) NormalizeImportedPhone(string? raw, string fieldName)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return (null, null);

        // NormalizeToDialable applies the shared plausibility floor, so an extension or a
        // 7-digit local fragment is still rejected rather than promoted to a number that
        // looks valid and reaches nobody.
        var normalized = PhoneValidation.NormalizeToDialable(trimmed);

        if (normalized == null)
        {
            return (null,
                $"{fieldName} '{trimmed}' could not be read as a phone number. "
                + "Use a full number such as (202) 555-0134 or +12025550134.");
        }

        return (normalized, null);
    }

    /// <summary>Names a row in an error message without assuming it has an email.</summary>
    private static string Describe(EmployeeRow row) =>
        !string.IsNullOrWhiteSpace(row.Email) ? row.Email!
        : !string.IsNullOrWhiteSpace(row.DisplayName) ? row.DisplayName!
        : $"{row.FirstName} {row.LastName}".Trim() is { Length: > 0 } name ? name
        : "(unnamed row)";

    private static (EmployeeRow? Record, string? Error) ParseEmployeeRow(Dictionary<string, string> row)
    {
        var azureAdId = row.GetValueOrDefault("azureAdObjectId", "")?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(azureAdId))
            azureAdId = $"csv-import-{Guid.NewGuid():N}";

        var firstName = row.GetValueOrDefault("firstName", "")?.Trim() ?? "";
        var lastName = row.GetValueOrDefault("lastName", "")?.Trim() ?? "";
        var displayName = row.GetValueOrDefault("displayName")?.Trim();
        var credentials = row.GetValueOrDefault("credentials")?.Trim();

        // A single "Name" column is read only when separate columns did not supply the
        // answer. Explicit firstName/lastName always win: the file said which is which,
        // and no parse beats being told.
        string? nameReviewReason = null;
        if (string.IsNullOrWhiteSpace(firstName) && string.IsNullOrWhiteSpace(lastName))
        {
            var combined = row.GetValueOrDefault("name")?.Trim();
            if (!string.IsNullOrWhiteSpace(combined))
            {
                var parsed = NameParser.Parse(combined);

                if (parsed.Confidence == NameConfidence.Low)
                {
                    // Deliberately NOT stored on a guess. A single word may be a unit
                    // label, and the department-contact branch below can still claim it;
                    // anything else is reported so a person decides, because filing
                    // someone under the wrong half of their name makes them unfindable.
                    nameReviewReason = parsed.ReviewReason;
                    displayName ??= parsed.DisplayName;
                }
                else
                {
                    firstName = parsed.FirstName;
                    lastName = parsed.LastName;
                    if (string.IsNullOrWhiteSpace(credentials)) credentials = parsed.Credentials;
                }
            }
        }

        // An extension is split off BEFORE normalization rather than failing the row.
        // "845-568-3434 x3434" used to be rejected outright as undialable, which lost the
        // whole contact; now the number and the extension are each kept in the field that
        // means them. A dedicated extension column wins over one found in the phone cell.
        var (officeRaw, officeExtension) =
            PhoneValidation.SplitExtension(row.GetValueOrDefault("officePhone"));
        var (mobileRaw, mobileExtension) =
            PhoneValidation.SplitExtension(row.GetValueOrDefault("mobilePhone"));

        var extensionCell = row.GetValueOrDefault("extension")?.Trim();
        var extension = !string.IsNullOrWhiteSpace(extensionCell)
            ? new string(extensionCell.Where(char.IsDigit).ToArray())
            : officeExtension ?? mobileExtension;
        if (string.IsNullOrWhiteSpace(extension)) extension = null;

        // Normalize rather than reject. A hospital's HR export contains "(202) 555-0134"
        // and "202-555-0134", not E.164 — insisting on canonical form made every row of a
        // real export fail, on the very path the onboarding standard recommends for staff
        // who are not in Entra. The same helper already normalizes numbers arriving from
        // Graph, so both ingestion paths now agree.
        var (officePhone, officeError) = NormalizeImportedPhone(officeRaw, "officePhone");
        if (officeError != null) return (null, officeError);

        var (mobilePhone, mobileError) = NormalizeImportedPhone(mobileRaw, "mobilePhone");
        if (mobileError != null) return (null, mobileError);

        var email = row.GetValueOrDefault("email", "")?.Trim() ?? "";

        // What kind of row is this? An explicit contactType column decides it; otherwise a
        // row with no person's name and no email, but a label and a way to be reached, is
        // a unit or service line ("3North", x3434) rather than a broken employee row.
        var declaredType = row.GetValueOrDefault("contactType")?.Trim();
        var reachable = !string.IsNullOrWhiteSpace(officePhone)
            || !string.IsNullOrWhiteSpace(mobilePhone)
            || !string.IsNullOrWhiteSpace(extension);

        var isDepartmentContact = !string.IsNullOrWhiteSpace(declaredType)
            ? string.Equals(declaredType, ContactType.Department, StringComparison.OrdinalIgnoreCase)
            : string.IsNullOrWhiteSpace(firstName)
              && string.IsNullOrWhiteSpace(lastName)
              && string.IsNullOrWhiteSpace(email)
              && !string.IsNullOrWhiteSpace(displayName)
              && reachable;

        if (isDepartmentContact)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return (null, "A department contact needs a name to show, e.g. '3North'.");
            if (!reachable)
                return (null, $"'{displayName}' has no phone number or extension, so nothing could reach it.");
        }
        else
        {
            // A name the parser would not commit to is reported as itself, rather than as
            // the missing-column error it used to masquerade as.
            if (nameReviewReason != null)
            {
                return (null, nameReviewReason
                    + " Split it into firstName and lastName columns, or write it as \"Last, First\".");
            }

            if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
                return (null, "firstName and lastName are required.");

            if (string.IsNullOrWhiteSpace(email))
            {
                return (null, "email is required. For a unit or service line with no mailbox, "
                    + "give it a displayName and a phone number or extension instead.");
            }
        }

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

        var departmentName = row.GetValueOrDefault("department")?.Trim();

        return (new EmployeeRow
        {
            AzureAdObjectId = azureAdId,
            FirstName = firstName,
            LastName = lastName,
            ContactType = isDepartmentContact ? ContactType.Department : ContactType.Person,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            Extension = extension,
            Credentials = string.IsNullOrWhiteSpace(credentials) ? null : credentials,
            Title = row.GetValueOrDefault("title")?.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? null : email,
            OfficePhone = string.IsNullOrWhiteSpace(officePhone) ? null : officePhone,
            MobilePhone = string.IsNullOrWhiteSpace(mobilePhone) ? null : mobilePhone,
            OfficeLocation = row.GetValueOrDefault("officeLocation")?.Trim(),
            DepartmentId = departmentId,
            DepartmentName = string.IsNullOrWhiteSpace(departmentName) ? null : departmentName,
            PresentColumns = new HashSet<string>(row.Keys, StringComparer.OrdinalIgnoreCase),
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

        /// <summary>Optional: a department/unit contact has no mailbox.</summary>
        public string? Email { get; init; }

        /// <summary>"Person" or "Department" -- see <see cref="Models.ContactType"/>.</summary>
        public string ContactType { get; init; } = Models.ContactType.Person;

        /// <summary>The label shown for a row that is not a person ("3North").</summary>
        public string? DisplayName { get; init; }

        /// <summary>Internal extension digits, held apart from the dialable number.</summary>
        public string? Extension { get; init; }

        /// <summary>Post-nominal letters lifted out of a combined name column.</summary>
        public string? Credentials { get; init; }

        public string? OfficePhone { get; init; }
        public string? MobilePhone { get; init; }
        public string? OfficeLocation { get; init; }
        public int? DepartmentId { get; set; }

        /// <summary>
        /// The department column's text, before it is resolved to an id. Held separately
        /// so an unresolved name is reported as such rather than silently dropped.
        /// </summary>
        public string? DepartmentName { get; init; }

        /// <summary>
        /// The column names this row's file actually supplied.
        ///
        /// A missing column and a deliberately blank cell both parsed to null, so an update
        /// wrote null for both. A narrower follow-up file therefore erased title, office
        /// phone, location and department while reporting complete success -- and clearing
        /// DepartmentId quietly drops staff out of department-scoped on-call lookups.
        /// </summary>
        public HashSet<string> PresentColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// The errors shown to the person importing. Capped, so an unsuitable file reports a
    /// legible problem instead of one line per row -- see <see cref="TotalErrors"/> for
    /// how many there actually were.
    /// </summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>
    /// How many errors the import found, including any beyond those listed in
    /// <see cref="Errors"/>. Never reports fewer than the list actually holds, so a
    /// result built anywhere else still carries an honest count.
    /// </summary>
    public int TotalErrors
    {
        get => Math.Max(_totalErrors, Errors.Count);
        set => _totalErrors = value;
    }

    private int _totalErrors;

    public bool IsValid { get; set; } = true;
}
