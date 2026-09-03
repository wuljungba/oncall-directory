using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services.Import;

/// <summary>
/// Runs an upload as a job: read every sheet, stage the rows, let the mapping be
/// corrected, show what will happen, then commit.
///
/// The one-shot importer could not serve the file people actually have -- a workbook of a
/// dozen unit rosters, with headers that differ between sheets and a handful of bad rows.
/// It read the first sheet and threw the rest away, and one unparseable row failed the
/// whole upload with nothing to act on.
///
/// The rows are staged EXACTLY as the file wrote them, so changing a mapping re-reads
/// them instead of asking for the file again, and the error report can hand back what was
/// uploaded with a reason beside it.
/// </summary>
public class ImportJobService
{
    private readonly AppDbContext _db;
    private readonly IAuditService? _audit;
    private readonly ILogger<ImportJobService> _logger;

    public ImportJobService(
        AppDbContext db, ILogger<ImportJobService> logger, IAuditService? audit = null)
    {
        _db = db;
        _logger = logger;
        _audit = audit;
    }

    /// <summary>How many sample rows a sheet preview shows.</summary>
    private const int SampleRows = 5;

    // ── Creating ──

    /// <summary>
    /// Reads an upload into a staged job. Nothing reaches the directory here.
    /// </summary>
    public async Task<(ImportJob? Job, string? Error)> CreateAsync(
        Stream file, string fileName, int? tenantId, string? uploadedByName,
        string? uploadedByPrincipalId, CancellationToken ct = default)
    {
        var (sheets, error) = await TabularFileReader.ReadAllSheetsAsync(file);
        if (error != null || sheets == null) return (null, error ?? "The file could not be read.");

        var job = new ImportJob
        {
            TenantId = tenantId,
            UploadedByName = uploadedByName,
            UploadedByPrincipalId = uploadedByPrincipalId,
            FileName = fileName,
            Status = ImportJobStatus.Draft,
            SheetCount = sheets.Count,
            TotalRows = sheets.Sum(s => s.Rows.Count),
        };

        // The suggested mapping is the existing header aliases, per sheet. Presenting it
        // rather than applying it invisibly is the point: a column the aliases do not
        // recognise used to be dropped without a word, and the person uploading had no
        // way to find out which one.
        var mapping = new Dictionary<string, Dictionary<string, string>>();
        foreach (var sheet in sheets)
        {
            mapping[sheet.Name] = sheet.Headers
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => BulkImportService.CanonicalHeader(g.Key));
        }

        job.MappingJson = JsonSerializer.Serialize(mapping);

        foreach (var sheet in sheets)
        {
            foreach (var row in sheet.Rows)
            {
                job.Rows.Add(new ImportJobRow
                {
                    SheetName = sheet.Name,
                    SheetIndex = sheet.Index,
                    SourceRow = row.Number,
                    RawValuesJson = JsonSerializer.Serialize(ToRawDictionary(sheet.Headers, row.Values)),
                });
            }
        }

        _db.ImportJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Import job {JobId} staged: {Sheets} sheet(s), {Rows} row(s) from {FileName}",
            job.Id, job.SheetCount, job.TotalRows, fileName);

        return (job, null);
    }

    // ── Mapping ──

    /// <summary>
    /// Replaces the mapping for one sheet, optionally for every sheet at once.
    ///
    /// Applying to all is the common case rather than a convenience: a workbook with one
    /// sheet per unit almost always repeats the same headers, and mapping twelve
    /// identical sheets by hand is twelve chances to do it differently.
    /// </summary>
    public async Task<bool> UpdateMappingAsync(
        ImportJob job, string sheetName, Dictionary<string, string> columns,
        bool applyToAllSheets, List<string>? excludedSheets, CancellationToken ct = default)
    {
        if (job.Status != ImportJobStatus.Draft) return false;

        var mapping = ReadMapping(job);
        var sheetNames = applyToAllSheets ? mapping.Keys.ToList() : [sheetName];

        foreach (var name in sheetNames)
        {
            if (!mapping.TryGetValue(name, out var existing)) continue;

            // Only columns this sheet actually has are touched, so "apply to all" over
            // sheets whose headers differ slightly leaves the others' own columns alone
            // instead of erasing them.
            foreach (var (column, field) in columns)
            {
                if (applyToAllSheets && !existing.ContainsKey(column)) continue;
                existing[column] = field;
            }
        }

        job.MappingJson = JsonSerializer.Serialize(mapping);
        if (excludedSheets != null)
            job.ExcludedSheetsJson = JsonSerializer.Serialize(excludedSheets);

        job.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── Preview ──

    /// <summary>
    /// Re-reads every staged row through the current mapping and reports what a commit
    /// would do, without doing any of it.
    /// </summary>
    public async Task<ImportJobPreview> BuildPreviewAsync(
        ImportJob job, List<int>? allowedTenantIds, CancellationToken ct = default)
    {
        var rows = await _db.ImportJobRows
            .Where(r => r.ImportJobId == job.Id)
            .OrderBy(r => r.SheetIndex).ThenBy(r => r.SourceRow)
            .ToListAsync(ct);

        var mapping = ReadMapping(job);
        var excluded = ReadExcludedSheets(job);
        var departmentsByName = await LoadDepartmentsAsync(job.TenantId, allowedTenantIds, ct);

        var parsed = new List<StagedRow>();

        foreach (var row in rows)
        {
            if (excluded.Contains(row.SheetName))
            {
                row.Included = false;
                row.ErrorReason = null;
                continue;
            }

            row.Included = row.Resolution != ImportRowResolution.Skip;

            var cells = MapCells(row, mapping, departmentsByName);
            var (record, error) = BulkImportService.ParseEmployeeRow(cells);

            row.ErrorReason = error;
            row.ReviewReason = record?.ReviewReason;

            if (record != null) parsed.Add(new StagedRow(row, record));
        }

        // Department names are resolved against the departments this import may file
        // against, exactly as the single-file path does.
        ResolveDepartments(parsed, departmentsByName);

        await MatchExistingAsync(parsed, allowedTenantIds, ct);

        DetectDuplicateEmailsWithinFile(parsed);

        await _db.SaveChangesAsync(ct);

        return BuildPreview(job, rows, mapping, excluded);
    }

    // ── Commit ──

    /// <summary>
    /// Writes the included rows, all of them or none.
    ///
    /// Errors were surfaced at preview and the user has already decided what to exclude,
    /// so reaching here with a bad row means something changed underneath -- and a
    /// half-written directory is worse than a refused one, because nobody can tell which
    /// half arrived.
    /// </summary>
    public async Task<(ImportResult Result, string? Error)> CommitAsync(
        ImportJob job, List<int>? allowedTenantIds, CancellationToken ct = default)
    {
        if (job.Status == ImportJobStatus.Committed)
            return (new ImportResult { IsValid = false, Errors = ["This import has already been committed."] }, null);

        var preview = await BuildPreviewAsync(job, allowedTenantIds, ct);

        var blocking = preview.Rows
            .Where(r => r.Included && r.ErrorReason != null)
            .Select(r => $"{r.SheetName} row {r.SourceRow}: {r.ErrorReason}")
            .ToList();

        if (blocking.Count > 0)
        {
            return (new ImportResult
            {
                TotalRows = preview.TotalRows,
                Imported = 0,
                Errors = blocking.Take(25).ToList(),
                TotalErrors = blocking.Count,
                IsValid = false,
            }, null);
        }

        var rows = await _db.ImportJobRows
            .Where(r => r.ImportJobId == job.Id && r.Included)
            .OrderBy(r => r.SheetIndex).ThenBy(r => r.SourceRow)
            .ToListAsync(ct);

        var mapping = ReadMapping(job);
        var departmentsByName = await LoadDepartmentsAsync(job.TenantId, allowedTenantIds, ct);
        var staged = new List<StagedRow>();
        foreach (var row in rows)
        {
            var (record, error) = BulkImportService.ParseEmployeeRow(MapCells(row, mapping, departmentsByName));
            if (error != null || record == null) continue;
            staged.Add(new StagedRow(row, record));
        }

        ResolveDepartments(staged, departmentsByName);

        // Resolved after the departments are known, and in one query: a per-row lookup
        // would issue one for every contact in the file.
        var extensionPrefixes = await DialPlan.ResolveExtensionPrefixesAsync(
            _db, job.TenantId,
            staged.Where(i => i.Record.DepartmentId.HasValue)
                .Select(i => i.Record.DepartmentId!.Value)
                .Distinct()
                .ToList(),
            ct);

        var imported = 0;
        foreach (var item in staged)
        {
            if (item.Job.Resolution == ImportRowResolution.Skip) continue;

            var matched = item.Job.MatchedEmployeeId.HasValue
                ? await FindInScopeAsync(item.Job.MatchedEmployeeId.Value, allowedTenantIds, ct)
                : null;

            var prefix = extensionPrefixes.For(item.Record.DepartmentId);

            if (matched != null && item.Job.Resolution == ImportRowResolution.Merge)
            {
                BulkImportService.ApplyRowToEmployee(item.Record, matched, prefix);
            }
            else
            {
                _db.Employees.Add(
                    BulkImportService.NewEmployeeFromRow(item.Record, job.TenantId, prefix));
            }

            imported++;
        }

        job.Status = ImportJobStatus.Committed;
        job.CommittedAt = DateTime.UtcNow;
        job.UpdatedAt = DateTime.UtcNow;
        job.ImportedCount = imported;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            _logger.LogError(ex, "Import job {JobId} failed to commit: {Message}", job.Id, message);
            return (new ImportResult { IsValid = false, Errors = [$"Database error: {message}"] }, null);
        }

        _logger.LogInformation("Import job {JobId} committed {Imported} row(s)", job.Id, imported);

        // Who added or changed directory entries, when, and how many. The row CONTENTS
        // are deliberately absent: an audit trail of who imported what does not need
        // names and numbers copied into a second table that outlives them.
        _audit?.Enqueue(new AuditLog
        {
            PrincipalId = job.UploadedByPrincipalId,
            UserName = job.UploadedByName ?? "(unknown)",
            Action = "Imported",
            ResourceType = "Employee",
            ResourceId = $"import-job-{job.Id}",
            TenantId = job.TenantId,
            Details =
                $"Imported {imported} of {job.TotalRows} row(s) from "
                + $"{job.SheetCount} sheet(s) of '{job.FileName}'.",
            Timestamp = DateTime.UtcNow,
        });

        return (new ImportResult
        {
            TotalRows = preview.TotalRows,
            Imported = imported,
            IsValid = true,
        }, null);
    }

    // ── Error report ──

    /// <summary>
    /// The rows that could not be imported, as they were uploaded, with a reason column.
    ///
    /// Handing back the original rows means the fix is to correct this file and upload it
    /// again, rather than to find the bad rows in the original by eye.
    /// </summary>
    public async Task<byte[]> BuildErrorReportAsync(ImportJob job, CancellationToken ct = default)
    {
        var rows = await _db.ImportJobRows
            .Where(r => r.ImportJobId == job.Id && (r.ErrorReason != null || r.ReviewReason != null))
            .OrderBy(r => r.SheetIndex).ThenBy(r => r.SourceRow)
            .ToListAsync(ct);

        // Every column any failed row carried, so nothing is lost from the round trip.
        var columns = new List<string>();
        foreach (var row in rows)
        {
            foreach (var key in ReadRaw(row).Keys)
                if (!columns.Contains(key)) columns.Add(key);
        }

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",",
            new[] { "Sheet", "Row", "Problem" }.Concat(columns).Select(CsvCell)));

        foreach (var row in rows)
        {
            var raw = ReadRaw(row);
            var cells = new List<string>
            {
                row.SheetName,
                row.SourceRow.ToString(),
                row.ErrorReason ?? row.ReviewReason ?? string.Empty,
            };
            cells.AddRange(columns.Select(c => raw.GetValueOrDefault(c, string.Empty)));

            csv.AppendLine(string.Join(",", cells.Select(CsvCell)));
        }

        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    // ── Row decisions ──

    /// <summary>Sets what happens to one staged row when it matches somebody already here.</summary>
    public async Task<bool> SetResolutionAsync(
        ImportJob job, int rowId, string resolution, CancellationToken ct = default)
    {
        if (job.Status != ImportJobStatus.Draft) return false;
        if (!ImportRowResolution.IsKnown(resolution)) return false;

        var row = await _db.ImportJobRows
            .FirstOrDefaultAsync(r => r.Id == rowId && r.ImportJobId == job.Id, ct);
        if (row == null) return false;

        row.Resolution = resolution;
        row.ResolutionChosen = true;
        row.Included = resolution != ImportRowResolution.Skip;
        job.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── Internals ──

    private sealed record StagedRow(ImportJobRow Job, BulkImportService.EmployeeRow Record);

    private static Dictionary<string, string> ToRawDictionary(string[] headers, string[] values)
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Length; i++)
        {
            var header = headers[i];
            if (string.IsNullOrWhiteSpace(header)) continue;

            var value = i < values.Length ? values[i].Trim().Trim('"') : string.Empty;

            // Two columns can carry the same heading. Never let a blank one overwrite a
            // populated one -- the same rule the single-file path uses.
            if (value.Length == 0 && raw.ContainsKey(header)) continue;
            raw[header] = value;
        }

        return raw;
    }

    private static Dictionary<string, string> ReadRaw(ImportJobRow row) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(row.RawValuesJson)
        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, Dictionary<string, string>> ReadMapping(ImportJob job) =>
        string.IsNullOrWhiteSpace(job.MappingJson)
            ? new Dictionary<string, Dictionary<string, string>>()
            : JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(job.MappingJson)
              ?? new Dictionary<string, Dictionary<string, string>>();

    private static HashSet<string> ReadExcludedSheets(ImportJob job) =>
        string.IsNullOrWhiteSpace(job.ExcludedSheetsJson)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(
                JsonSerializer.Deserialize<List<string>>(job.ExcludedSheetsJson) ?? [],
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Turns one staged row's original cells into the canonical fields the parser wants,
    /// through this sheet's mapping.
    /// </summary>
    private static Dictionary<string, string> MapCells(
        ImportJobRow row,
        Dictionary<string, Dictionary<string, string>> mapping,
        IReadOnlyDictionary<string, int> departmentsByName)
    {
        var raw = ReadRaw(row);
        mapping.TryGetValue(row.SheetName, out var columns);

        var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (header, value) in raw)
        {
            var field = columns != null && columns.TryGetValue(header, out var mapped)
                ? mapped
                : BulkImportService.CanonicalHeader(header);

            // An explicit empty mapping is the user saying "ignore this column".
            if (string.IsNullOrWhiteSpace(field)) continue;

            if (value.Length == 0 && cells.ContainsKey(field)) continue;
            cells[field] = value;
        }

        // A workbook with one sheet per unit writes the unit in the TAB, not in a column.
        // The sheet name is used only when it actually names a department that exists:
        // defaulting to it unconditionally would fail every row of an ordinary file whose
        // tab is called "Sheet1".
        var hasDepartment = cells.ContainsKey("department") || cells.ContainsKey("departmentId");
        if (!hasDepartment && departmentsByName.ContainsKey(DepartmentKey(row.SheetName)))
            cells["department"] = row.SheetName;

        return cells;
    }

    private async Task<Dictionary<string, int>> LoadDepartmentsAsync(
        int? tenantId, List<int>? allowedTenantIds, CancellationToken ct)
    {
        var query = _db.Departments.AsNoTracking();
        if (tenantId.HasValue)
            query = query.Where(d => d.TenantId == tenantId.Value);
        else if (allowedTenantIds is { Count: > 0 })
            query = query.Where(d => d.TenantId.HasValue && allowedTenantIds.Contains(d.TenantId.Value));

        var departments = await query.Select(d => new { d.Id, d.Name }).ToListAsync(ct);

        var byName = new Dictionary<string, int>();
        foreach (var d in departments) byName[DepartmentKey(d.Name)] = d.Id;
        return byName;
    }

    /// <summary>Compared the way people type them: casing and spacing vary between exports.</summary>
    private static string DepartmentKey(string name) =>
        new string(name.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    private static void ResolveDepartments(
        List<StagedRow> staged, IReadOnlyDictionary<string, int> departmentsByName)
    {
        foreach (var item in staged)
        {
            if (item.Record.DepartmentId.HasValue) continue;
            if (string.IsNullOrWhiteSpace(item.Record.DepartmentName)) continue;

            if (departmentsByName.TryGetValue(DepartmentKey(item.Record.DepartmentName!), out var id))
            {
                item.Record.DepartmentId = id;
            }
            else if (item.Job.ErrorReason == null)
            {
                // Never created from a file. Departments drive on-call routing, and a
                // misspelling would quietly split one department into two.
                item.Job.ErrorReason =
                    $"Department '{item.Record.DepartmentName}' does not exist. Create it first, "
                    + "or map that column to something else.";
            }
        }
    }

    /// <summary>
    /// Finds the directory entry each row would land on, so the preview can say whether it
    /// is adding somebody or changing them.
    ///
    /// Every lookup is restricted to the tenants the caller may modify. Without that a
    /// scoped admin could resolve -- and then overwrite -- another customer's record.
    /// </summary>
    private async Task MatchExistingAsync(
        List<StagedRow> staged, List<int>? allowedTenantIds, CancellationToken ct)
    {
        if (staged.Count == 0) return;

        var candidates = _db.Employees.AsNoTracking();
        if (allowedTenantIds != null)
        {
            candidates = candidates.Where(e =>
                e.TenantId.HasValue && allowedTenantIds.Contains(e.TenantId.Value));
        }

        var existing = await candidates
            .Select(e => new
            {
                e.Id, e.Email, e.LastName, e.OfficePhone, e.MobilePhone, e.Extension,
            })
            .ToListAsync(ct);

        var byEmail = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in existing)
            if (!string.IsNullOrWhiteSpace(e.Email)) byEmail[e.Email!] = e.Id;

        foreach (var item in staged)
        {
            var record = item.Record;

            // 1. The address, when there is one. Two absent addresses are not a match.
            if (!string.IsNullOrWhiteSpace(record.Email)
                && byEmail.TryGetValue(record.Email!, out var byEmailId))
            {
                Match(item, byEmailId, "email");
                continue;
            }

            // 2. Surname plus the last four digits of a number. Weaker, and offered as a
            //    choice rather than applied: two Smiths can share a department line.
            var lastFour = LastFour(record.OfficePhone) ?? LastFour(record.MobilePhone);
            if (!string.IsNullOrWhiteSpace(record.LastName) && lastFour != null)
            {
                var hit = existing.FirstOrDefault(e =>
                    string.Equals(e.LastName, record.LastName, StringComparison.OrdinalIgnoreCase)
                    && (LastFour(e.OfficePhone) == lastFour || LastFour(e.MobilePhone) == lastFour));

                if (hit != null)
                {
                    Match(item, hit.Id, "surname and phone");
                    continue;
                }
            }

            // 3. A number and an extension together, which is how a unit repeats.
            if (!string.IsNullOrWhiteSpace(record.Extension))
            {
                var hit = existing.FirstOrDefault(e =>
                    e.Extension == record.Extension
                    && (e.OfficePhone == record.OfficePhone || record.OfficePhone == null));

                if (hit != null) Match(item, hit.Id, "phone and extension");
            }
        }

        static void Match(StagedRow item, Guid id, string how)
        {
            item.Job.MatchedEmployeeId = id;
            item.Job.MatchedOn = how;

            // Merging is the safe default: creating a second record for somebody already
            // in the directory is how a code call reaches the number nobody answers.
            //
            // Only the DEFAULT is overridden. This runs on every preview rebuild -- and
            // CommitAsync rebuilds the preview before writing -- so without the flag an
            // explicit "these are two different people" was reverted to merge on the way
            // to the database, silently collapsing two contacts into one.
            if (!item.Job.ResolutionChosen && item.Job.Resolution == ImportRowResolution.Create)
                item.Job.Resolution = ImportRowResolution.Merge;
        }
    }

    private static string? LastFour(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 ? digits[^4..] : null;
    }

    /// <summary>
    /// Two rows in one upload claiming the same address is a mistake in the file, and the
    /// unique index would reject the second one anyway -- as a database error nobody can
    /// act on rather than a row number.
    /// </summary>
    private static void DetectDuplicateEmailsWithinFile(List<StagedRow> staged)
    {
        var seen = new Dictionary<string, StagedRow>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in staged)
        {
            var email = item.Record.Email;
            if (string.IsNullOrWhiteSpace(email)) continue;
            if (item.Job.Resolution == ImportRowResolution.Skip) continue;

            if (seen.TryGetValue(email!, out var first))
            {
                item.Job.ErrorReason ??=
                    $"'{email}' also appears on {first.Job.SheetName} row {first.Job.SourceRow}. "
                    + "One address belongs to one person.";
            }
            else
            {
                seen[email!] = item;
            }
        }
    }

    private async Task<Employee?> FindInScopeAsync(
        Guid id, List<int>? allowedTenantIds, CancellationToken ct)
    {
        var query = _db.Employees.Where(e => e.Id == id);
        if (allowedTenantIds != null)
        {
            query = query.Where(e =>
                e.TenantId.HasValue && allowedTenantIds.Contains(e.TenantId.Value));
        }

        return await query.FirstOrDefaultAsync(ct);
    }

    private static ImportJobPreview BuildPreview(
        ImportJob job, List<ImportJobRow> rows,
        Dictionary<string, Dictionary<string, string>> mapping,
        HashSet<string> excluded)
    {
        var sheets = rows
            .GroupBy(r => new { r.SheetName, r.SheetIndex })
            .OrderBy(g => g.Key.SheetIndex)
            .Select(g => new ImportSheetPreview
            {
                Name = g.Key.SheetName,
                Index = g.Key.SheetIndex,
                RowCount = g.Count(),
                Included = !excluded.Contains(g.Key.SheetName),
                Columns = mapping.TryGetValue(g.Key.SheetName, out var columns)
                    ? columns.Select(c => new ImportColumnMapping { Column = c.Key, Field = c.Value }).ToList()
                    : [],
                SampleRows = g.OrderBy(r => r.SourceRow).Take(SampleRows)
                    .Select(ReadRaw).ToList(),
            })
            .ToList();

        return new ImportJobPreview
        {
            JobId = job.Id,
            FileName = job.FileName,
            Status = job.Status,
            SheetCount = job.SheetCount,
            TotalRows = job.TotalRows,
            Sheets = sheets,
            Rows = rows.Select(r => new ImportRowPreview
            {
                Id = r.Id,
                SheetName = r.SheetName,
                SourceRow = r.SourceRow,
                Included = r.Included,
                ErrorReason = r.ErrorReason,
                ReviewReason = r.ReviewReason,
                Resolution = r.Resolution,
                MatchedOn = r.MatchedOn,
            }).ToList(),
        };
    }

    private static string CsvCell(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains(',') || text.Contains('"') || text.Contains('\n')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }
}

// ── Preview shapes ──

public class ImportJobPreview
{
    public int JobId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int SheetCount { get; set; }
    public int TotalRows { get; set; }
    public List<ImportSheetPreview> Sheets { get; set; } = [];
    public List<ImportRowPreview> Rows { get; set; } = [];

    public int ReadyCount => Rows.Count(r => r.Included && r.ErrorReason == null);
    public int ErrorCount => Rows.Count(r => r.Included && r.ErrorReason != null);
    public int ReviewCount => Rows.Count(r => r.Included && r.ErrorReason == null && r.ReviewReason != null);
    public int MergeCount => Rows.Count(r => r.Included && r.Resolution == ImportRowResolution.Merge);
}

public class ImportSheetPreview
{
    public string Name { get; set; } = string.Empty;
    public int Index { get; set; }
    public int RowCount { get; set; }
    public bool Included { get; set; }
    public List<ImportColumnMapping> Columns { get; set; } = [];
    public List<Dictionary<string, string>> SampleRows { get; set; } = [];
}

public class ImportColumnMapping
{
    public string Column { get; set; } = string.Empty;

    /// <summary>The canonical field, or empty to ignore this column.</summary>
    public string Field { get; set; } = string.Empty;
}

public class ImportRowPreview
{
    public int Id { get; set; }
    public string SheetName { get; set; } = string.Empty;
    public int SourceRow { get; set; }
    public bool Included { get; set; }
    public string? ErrorReason { get; set; }
    public string? ReviewReason { get; set; }
    public string Resolution { get; set; } = ImportRowResolution.Create;
    public string? MatchedOn { get; set; }
}
