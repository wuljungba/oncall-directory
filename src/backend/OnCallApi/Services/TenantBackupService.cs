using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Authorization;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

public interface ITenantBackupService
{
    /// <summary>Everything one subscription owns, as a portable archive.</summary>
    Task<TenantBackup> ExportAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Merges an archive back into a subscription. Never deletes, never overwrites a record
    /// that is already there, and refuses to recreate history — see <see cref="TenantRestoreReport"/>.
    /// </summary>
    Task<TenantRestoreReport> RestoreAsync(int tenantId, TenantBackup backup, CancellationToken ct = default);
}

/// <summary>
/// Lets a subscription take its own data out, and put it back.
///
/// <para>The database backups protect the deployment. They do not help one customer, because
/// every tenant shares one database: a point-in-time restore rolls back every other customer
/// too, so it can never be the answer to "we deleted our directory by mistake".</para>
///
/// <para>That gap is widest exactly where it is least survivable. A subscription whose staff
/// came from Entra can be rebuilt by re-syncing the directory — the authoritative copy is
/// somebody else's. A subscription whose staff were uploaded from a spreadsheet has no
/// upstream at all: this database IS the only copy, and the spreadsheet it came from is on
/// somebody's laptop, months stale, if it exists. For those customers an export is not a
/// convenience, it is the entire disaster-recovery story.</para>
///
/// <para>Restore deliberately does less than export. Configuration — people, departments,
/// schedules, code trees — merges back. Records of things that happened do not: see
/// <see cref="RestoreAsync"/>.</para>
/// </summary>
public class TenantBackupService : ITenantBackupService
{
    /// <summary>
    /// Bumped when the shape changes incompatibly. An archive that cannot be read is worse
    /// than no archive, because it was trusted in the meantime.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    private readonly AppDbContext _db;
    private readonly ILogger<TenantBackupService> _logger;

    public TenantBackupService(AppDbContext db, ILogger<TenantBackupService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<TenantBackup> ExportAsync(int tenantId, CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new KeyNotFoundException($"Tenant {tenantId} not found");

        // Departments are the spine: schedules, phone trees, locations and escalation policies
        // all hang off a department rather than carrying a tenant id of their own.
        var departments = await _db.Departments.AsNoTracking()
            .Where(d => d.TenantId == tenantId).ToListAsync(ct);
        var departmentIds = departments.Select(d => d.Id).ToList();

        var employees = await _db.Employees.AsNoTracking()
            .Include(e => e.Department)
            .Where(e => e.TenantId == tenantId).ToListAsync(ct);
        var employeeIds = employees.Select(e => e.Id).ToList();

        var schedules = await _db.Schedules.AsNoTracking()
            .Where(s => departmentIds.Contains(s.DepartmentId))
            .ToListAsync(ct);
        var scheduleIds = schedules.Select(s => s.Id).ToList();

        var shifts = await _db.Shifts.AsNoTracking()
            .Include(s => s.Employee)
            .Where(s => scheduleIds.Contains(s.ScheduleId)).ToListAsync(ct);

        var phoneTrees = await _db.PhoneTrees.AsNoTracking()
            .Where(t => t.DepartmentId != null && departmentIds.Contains(t.DepartmentId.Value))
            .ToListAsync(ct);
        var treeIds = phoneTrees.Select(t => t.Id).ToList();

        var nodes = await _db.PhoneTreeNodes.AsNoTracking()
            .Where(n => treeIds.Contains(n.PhoneTreeId)).ToListAsync(ct);

        var timeOff = await _db.TimeOffs.AsNoTracking()
            .Include(t => t.Employee)
            .Where(t => employeeIds.Contains(t.EmployeeId)).ToListAsync(ct);

        // Settings, with anything secret left out rather than masked. A backup file is copied
        // to laptops and mailed to people; a client secret that travels in one has escaped,
        // and a masked placeholder restored later would silently break the integration it
        // claims to configure. The key is kept so it is obvious something was withheld.
        var settings = await _db.AppSettings.AsNoTracking()
            .Where(s => s.TenantId == tenantId).ToListAsync(ct);

        var exportedSettings = settings.Select(s => new BackupSetting(
            s.Key,
            AppSettingSensitivity.IsSensitive(s.Key) ? null : s.Value,
            AppSettingSensitivity.IsSensitive(s.Key))).ToList();

        var redacted = exportedSettings.Count(s => s.Withheld);

        // The record half of the archive. Exported so the customer holds their own history;
        // NOT restorable, for the reason given on RestoreAsync.
        var incidents = await _db.PhoneTreeEvents.AsNoTracking()
            .Include(e => e.DebriefLog)
            .Include(e => e.DispatchSteps)
            .Where(e => treeIds.Contains(e.PhoneTreeId))
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        _logger.LogInformation(
            "Exported tenant {TenantId}: {Employees} employee(s), {Departments} department(s), "
            + "{Schedules} schedule(s), {Shifts} shift(s), {Trees} phone tree(s), {Incidents} incident(s), "
            + "{Redacted} setting(s) withheld as sensitive",
            tenantId, employees.Count, departments.Count, schedules.Count, shifts.Count,
            phoneTrees.Count, incidents.Count, redacted);

        return new TenantBackup
        {
            FormatVersion = CurrentFormatVersion,
            ExportedAt = DateTime.UtcNow,
            TenantId = tenant.Id,
            TenantName = tenant.Name,
            Departments = departments.Select(BackupDepartment.From).ToList(),
            Employees = employees.Select(BackupEmployee.From).ToList(),
            Schedules = schedules.Select(s => BackupSchedule.From(s, departments)).ToList(),
            Shifts = shifts.Select(s => BackupShift.From(s, schedules)).ToList(),
            PhoneTrees = phoneTrees.Select(t => BackupPhoneTree.From(t, departments)).ToList(),
            PhoneTreeNodes = nodes.Select(n => BackupPhoneTreeNode.From(n, phoneTrees)).ToList(),
            TimeOff = timeOff.Select(BackupTimeOff.From).ToList(),
            Settings = exportedSettings,
            Incidents = incidents.Select(BackupIncident.From).ToList(),
        };
    }

    /// <summary>
    /// Puts configuration back, and only configuration.
    ///
    /// <para><b>Merges, never replaces.</b> A record that is already present is left exactly as
    /// it is and counted as skipped. Restore is reached for when something has been lost, often
    /// in a hurry and often more than once — so running it twice must be safe, and it must
    /// never be capable of destroying what survived.</para>
    ///
    /// <para><b>History is not restorable, by design.</b> Incidents, debrief notes, dispatch
    /// steps and audit rows are in the archive so the customer holds their own record, but this
    /// will not write them back. Accepting them would mean an emergency page, its timeline and
    /// its debrief could be conjured from an uploaded file — the audit trail would become
    /// something anybody holding a JSON file could author, which is worth more than the
    /// convenience of restoring it. Recovering genuine incident history is a database restore,
    /// performed by an operator, from a backup nobody could edit.</para>
    /// </summary>
    public async Task<TenantRestoreReport> RestoreAsync(
        int tenantId, TenantBackup backup, CancellationToken ct = default)
    {
        if (backup.FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"This archive is format version {backup.FormatVersion}, but this deployment only "
                + $"understands up to {CurrentFormatVersion}. Restoring it could silently drop data — "
                + "upgrade OnCall first.");
        }

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new KeyNotFoundException($"Tenant {tenantId} not found");

        var report = new TenantRestoreReport { TenantId = tenantId, TenantName = tenant.Name };

        // ── Departments, matched by name within this tenant ──
        var existingDepartments = await _db.Departments
            .Where(d => d.TenantId == tenantId).ToListAsync(ct);

        foreach (var d in backup.Departments)
        {
            var match = existingDepartments.FirstOrDefault(
                x => string.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase));

            if (match != null) { report.DepartmentsSkipped++; continue; }

            var created = new Department
            {
                Name = d.Name,
                Description = d.Description,
                Category = d.Category,
                AzureAdGroupId = d.AzureAdGroupId,
                TenantId = tenantId,
                IsActive = d.IsActive,
            };
            _db.Departments.Add(created);
            existingDepartments.Add(created);
            report.DepartmentsCreated++;
        }

        await _db.SaveChangesAsync(ct);

        var departmentsByName = existingDepartments
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        // ── Employees, matched by email then by directory object id ──
        var existingEmployees = await _db.Employees
            .Where(e => e.TenantId == tenantId).ToListAsync(ct);

        foreach (var e in backup.Employees)
        {
            var match = existingEmployees.FirstOrDefault(x =>
                (!string.IsNullOrWhiteSpace(e.Email)
                    && string.Equals(x.Email, e.Email, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(e.AzureAdObjectId)
                    && x.AzureAdObjectId == e.AzureAdObjectId));

            if (match != null) { report.EmployeesSkipped++; continue; }

            _db.Employees.Add(new Employee
            {
                AzureAdObjectId = e.AzureAdObjectId ?? string.Empty,
                FirstName = e.FirstName ?? string.Empty,
                LastName = e.LastName ?? string.Empty,
                DisplayName = e.DisplayName,
                Title = e.Title,
                Credentials = e.Credentials,
                Specialty = e.Specialty,
                ClinicalRole = e.ClinicalRole,
                Email = e.Email,
                OfficePhone = e.OfficePhone,
                MobilePhone = e.MobilePhone,
                PagerNumber = e.PagerNumber,
                Extension = e.Extension,
                ContactType = e.ContactType ?? Models.ContactType.Person,
                OfficeLocation = e.OfficeLocation,
                DepartmentId = ResolveDepartment(e.DepartmentName, departmentsByName),
                TenantId = tenantId,
                Certifications = e.Certifications,
                Languages = e.Languages,
                IsActive = e.IsActive,
                // Restored, not synced and not typed in by an administrator just now. Source
                // decides how later syncs treat a record, so inventing "Ad" here would hand
                // a directory sync licence to deactivate someone it has never seen.
                Source = string.IsNullOrWhiteSpace(e.Source) ? "Local" : e.Source,
                CreatedAt = e.CreatedAt == default ? DateTime.UtcNow : e.CreatedAt,
                UpdatedAt = DateTime.UtcNow,
            });
            report.EmployeesCreated++;
        }

        await _db.SaveChangesAsync(ct);

        // ── Phone trees, matched by name within the tenant's departments ──
        var tenantDepartmentIds = departmentsByName.Values.ToList();
        var existingTrees = await _db.PhoneTrees
            .Where(t => t.DepartmentId != null && tenantDepartmentIds.Contains(t.DepartmentId.Value))
            .ToListAsync(ct);

        foreach (var t in backup.PhoneTrees)
        {
            if (existingTrees.Any(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase)))
            {
                report.PhoneTreesSkipped++;
                continue;
            }

            var created = new PhoneTree
            {
                Name = t.Name,
                TreeType = t.TreeType,
                DepartmentId = ResolveDepartment(t.DepartmentName, departmentsByName),
                Procedure = t.Procedure,
                FallbackProcedure = t.FallbackProcedure,
                IsActive = t.IsActive,
            };
            _db.PhoneTrees.Add(created);
            existingTrees.Add(created);
            report.PhoneTreesCreated++;
        }

        await _db.SaveChangesAsync(ct);

        var treesByName = existingTrees
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var existingNodeKeys = (await _db.PhoneTreeNodes
                .Where(n => treesByName.Values.Contains(n.PhoneTreeId))
                .Select(n => new { n.PhoneTreeId, n.Order })
                .ToListAsync(ct))
            .Select(n => (n.PhoneTreeId, n.Order))
            .ToHashSet();

        foreach (var n in backup.PhoneTreeNodes)
        {
            if (n.PhoneTreeName == null || !treesByName.TryGetValue(n.PhoneTreeName, out var treeId))
            {
                report.PhoneTreeNodesSkipped++;
                continue;
            }

            if (!existingNodeKeys.Add((treeId, n.Order))) { report.PhoneTreeNodesSkipped++; continue; }

            _db.PhoneTreeNodes.Add(new PhoneTreeNode
            {
                PhoneTreeId = treeId,
                Order = n.Order,
                RoleName = n.RoleName,
                Condition = n.Condition,
            });
            report.PhoneTreeNodesCreated++;
        }

        await _db.SaveChangesAsync(ct);

        // ── What was deliberately not written back ──
        report.IncidentsInArchive = backup.Incidents.Count;
        report.SettingsWithheldInArchive = backup.Settings.Count(s => s.Withheld);

        _logger.LogInformation(
            "Restored into tenant {TenantId}: +{Departments} department(s), +{Employees} employee(s), "
            + "+{Trees} phone tree(s), +{Nodes} node(s). {Incidents} incident(s) in the archive were "
            + "NOT written back — history is not restorable from a file.",
            tenantId, report.DepartmentsCreated, report.EmployeesCreated,
            report.PhoneTreesCreated, report.PhoneTreeNodesCreated, report.IncidentsInArchive);

        return report;
    }

    private static int? ResolveDepartment(string? name, Dictionary<string, int> byName) =>
        !string.IsNullOrWhiteSpace(name) && byName.TryGetValue(name, out var id) ? id : null;

    /// <summary>Indented so a human opening the file can read it. These are read in a crisis.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
