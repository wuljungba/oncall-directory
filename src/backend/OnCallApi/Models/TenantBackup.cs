namespace OnCallApi.Models;

/// <summary>
/// One subscription's data, in a form that outlives this database.
///
/// Records reference each other by NAME rather than by primary key, on purpose. A backup whose
/// rows point at integer ids can only be restored into a database where those ids still mean
/// the same thing — which is exactly not the case after the loss that made someone reach for
/// it. Names survive being restored into a fresh subscription, a different deployment, or a
/// rebuilt database.
///
/// It is also plain, indented JSON so that a person can open it, read it, and pull one phone
/// number out of it with no software at all. That is a real recovery mode.
/// </summary>
public class TenantBackup
{
    public int FormatVersion { get; set; } = 1;
    public DateTime ExportedAt { get; set; }

    /// <summary>Recorded for provenance only; a restore writes into whichever tenant it targets.</summary>
    public int TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;

    public List<BackupDepartment> Departments { get; set; } = new();
    public List<BackupEmployee> Employees { get; set; } = new();
    public List<BackupSchedule> Schedules { get; set; } = new();
    public List<BackupShift> Shifts { get; set; } = new();
    public List<BackupPhoneTree> PhoneTrees { get; set; } = new();
    public List<BackupPhoneTreeNode> PhoneTreeNodes { get; set; } = new();
    public List<BackupTimeOff> TimeOff { get; set; } = new();
    public List<BackupSetting> Settings { get; set; } = new();

    /// <summary>
    /// Code-call history. Exported so the customer holds their own record; never written back
    /// by a restore — see <c>TenantBackupService.RestoreAsync</c>.
    /// </summary>
    public List<BackupIncident> Incidents { get; set; } = new();
}

public class BackupDepartment
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? AzureAdGroupId { get; set; }
    public bool IsActive { get; set; }

    public static BackupDepartment From(Department d) => new()
    {
        Name = d.Name,
        Description = d.Description,
        Category = d.Category,
        AzureAdGroupId = d.AzureAdGroupId,
        IsActive = d.IsActive,
    };
}

public class BackupEmployee
{
    public string? AzureAdObjectId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DisplayName { get; set; }
    public string? Title { get; set; }
    public string? Credentials { get; set; }
    public string? Specialty { get; set; }
    public string? ClinicalRole { get; set; }
    public string? Email { get; set; }
    public string? OfficePhone { get; set; }
    public string? MobilePhone { get; set; }
    public string? PagerNumber { get; set; }
    public string? Extension { get; set; }
    public string? ContactType { get; set; }
    public string? OfficeLocation { get; set; }
    public string? DepartmentName { get; set; }
    public string? Certifications { get; set; }
    public string? Languages { get; set; }
    public bool IsActive { get; set; }

    /// <summary>"Ad", "CsvImport" or "Local" — it decides how a later sync may treat the record.</summary>
    public string? Source { get; set; }
    public DateTime CreatedAt { get; set; }

    public static BackupEmployee From(Employee e) => new()
    {
        AzureAdObjectId = e.AzureAdObjectId,
        FirstName = e.FirstName,
        LastName = e.LastName,
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
        ContactType = e.ContactType,
        OfficeLocation = e.OfficeLocation,
        DepartmentName = e.Department?.Name,
        Certifications = e.Certifications,
        Languages = e.Languages,
        IsActive = e.IsActive,
        Source = e.Source,
        CreatedAt = e.CreatedAt,
    };
}

public class BackupSchedule
{
    public string Name { get; set; } = string.Empty;
    public string? DepartmentName { get; set; }
    public string? RotationType { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; }

    public static BackupSchedule From(Schedule s, IEnumerable<Department> departments) => new()
    {
        Name = s.Name,
        DepartmentName = departments.FirstOrDefault(d => d.Id == s.DepartmentId)?.Name,
        RotationType = s.RotationType,
        StartDate = s.StartDate,
        EndDate = s.EndDate,
        Notes = s.Notes,
        IsActive = s.IsActive,
    };
}

/// <summary>
/// A worked or planned shift. Carries the assignee's address rather than their row id, so it
/// still identifies a person after a restore into a rebuilt database.
/// </summary>
public class BackupShift
{
    public string? ScheduleName { get; set; }
    public string? EmployeeEmail { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string? Tier { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }

    public static BackupShift From(Shift s, IEnumerable<Schedule> schedules) => new()
    {
        ScheduleName = schedules.FirstOrDefault(x => x.Id == s.ScheduleId)?.Name,
        EmployeeEmail = s.Employee?.Email,
        StartTime = s.StartTime,
        EndTime = s.EndTime,
        Tier = s.Tier,
        Status = s.Status,
        Notes = s.Notes,
    };
}

public class BackupPhoneTree
{
    public string Name { get; set; } = string.Empty;
    public string TreeType { get; set; } = string.Empty;
    public string? DepartmentName { get; set; }
    public string? Procedure { get; set; }
    public string? FallbackProcedure { get; set; }
    public bool IsActive { get; set; }

    public static BackupPhoneTree From(PhoneTree t, IEnumerable<Department> departments) => new()
    {
        Name = t.Name,
        TreeType = t.TreeType,
        DepartmentName = departments.FirstOrDefault(d => d.Id == t.DepartmentId)?.Name,
        Procedure = t.Procedure,
        FallbackProcedure = t.FallbackProcedure,
        IsActive = t.IsActive,
    };
}

public class BackupPhoneTreeNode
{
    public string? PhoneTreeName { get; set; }
    public int Order { get; set; }
    public string? RoleName { get; set; }
    public string? Condition { get; set; }

    public static BackupPhoneTreeNode From(PhoneTreeNode n, IEnumerable<PhoneTree> trees) => new()
    {
        PhoneTreeName = trees.FirstOrDefault(t => t.Id == n.PhoneTreeId)?.Name,
        Order = n.Order,
        RoleName = n.RoleName,
        Condition = n.Condition,
    };
}

public class BackupTimeOff
{
    public string? EmployeeEmail { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string? Type { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }

    public static BackupTimeOff From(TimeOff t) => new()
    {
        EmployeeEmail = t.Employee?.Email,
        StartDate = t.StartDate,
        EndDate = t.EndDate,
        Type = t.Type,
        Status = t.Status,
        Notes = t.Notes,
    };
}

/// <summary>
/// One configuration setting. A sensitive one travels with its value withheld rather than
/// masked: a masked placeholder restored later would look configured and not work.
/// </summary>
public record BackupSetting(string Key, string? Value, bool Withheld);

/// <summary>One code call, as a record. Never written back by a restore.</summary>
public class BackupIncident
{
    public int Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? Status { get; set; }
    public string? Location { get; set; }
    public int? ResponseTimeSeconds { get; set; }
    public string? InitiatedByName { get; set; }
    public string? InitiatedByEmail { get; set; }
    public string? RequestedByName { get; set; }
    public string? NotifiedByName { get; set; }
    public string? Outcome { get; set; }
    public string? Notes { get; set; }
    public List<BackupDebriefNote> DebriefLog { get; set; } = new();

    public static BackupIncident From(PhoneTreeEvent e) => new()
    {
        Id = e.Id,
        StartedAt = e.StartedAt,
        EndedAt = e.EndedAt,
        AcknowledgedAt = e.AcknowledgedAt,
        Status = e.Status,
        Location = e.Location,
        ResponseTimeSeconds = e.ResponseTimeSeconds,
        InitiatedByName = e.InitiatedByName,
        InitiatedByEmail = e.InitiatedByEmail,
        RequestedByName = e.RequestedByName,
        NotifiedByName = e.NotifiedByName,
        Outcome = e.Outcome,
        Notes = e.Notes,
        DebriefLog = e.DebriefLog
            .OrderBy(n => n.CreatedAt)
            .Select(n => new BackupDebriefNote
            {
                Note = n.Note,
                AuthorName = n.AuthorName,
                CreatedAt = n.CreatedAt,
            }).ToList(),
    };
}

public class BackupDebriefNote
{
    public string Note { get; set; } = string.Empty;
    public string? AuthorName { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>What a restore actually did, itemised. Reported rather than summarised as "done".</summary>
public class TenantRestoreReport
{
    public int TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;

    public int DepartmentsCreated { get; set; }
    public int DepartmentsSkipped { get; set; }
    public int EmployeesCreated { get; set; }
    public int EmployeesSkipped { get; set; }
    public int PhoneTreesCreated { get; set; }
    public int PhoneTreesSkipped { get; set; }
    public int PhoneTreeNodesCreated { get; set; }
    public int PhoneTreeNodesSkipped { get; set; }

    /// <summary>Present in the archive and deliberately NOT written back.</summary>
    public int IncidentsInArchive { get; set; }

    /// <summary>Sensitive settings that were withheld at export and must be re-entered by hand.</summary>
    public int SettingsWithheldInArchive { get; set; }

    /// <summary>Said plainly, because "restored" would otherwise imply these came back too.</summary>
    public List<string> NotRestored { get; set; } = new();
}
