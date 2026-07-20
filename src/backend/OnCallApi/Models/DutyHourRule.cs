namespace OnCallApi.Models;

/// <summary>
/// Defines a duty-hour compliance rule (e.g., 80-hour weekly limit, 10-hour rest).
/// Rules are configurable per role/department and enforced during scheduling.
/// </summary>
public class DutyHourRule
{
    public int Id { get; set; }

    [Required(AllowEmptyStrings = false)]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Maximum hours allowed within the period (e.g., 80).</summary>
    public int MaxHoursPerPeriod { get; set; } = 80;

    /// <summary>Period in days for the limit (e.g., 7 for a rolling week).</summary>
    public int PeriodDays { get; set; } = 7;

    /// <summary>Minimum rest hours between shifts (e.g., 10 for ACGME).</summary>
    public int MinHoursBetweenShifts { get; set; } = 10;

    /// <summary>Maximum consecutive shift length in hours (e.g., 24).</summary>
    public int MaxShiftLengthHours { get; set; } = 24;

    /// <summary>Maximum consecutive days worked (e.g., 7).</summary>
    public int MaxConsecutiveDays { get; set; } = 7;

    /// <summary>JSON array of clinical roles this rule applies to (null = all).</summary>
    [MaxLength(1000)]
    public string? ApplicableRoles { get; set; }

    /// <summary>Optional department scope (null = organization-wide).</summary>
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Records a compliance violation for auditing and reporting.
/// </summary>
public class DutyHourViolation
{
    public int Id { get; set; }
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public int RuleId { get; set; }
    public DutyHourRule Rule { get; set; } = null!;
    public string Description { get; set; } = string.Empty;
    public int Severity { get; set; } = 1; // 1=warning, 2=breach
    public bool IsResolved { get; set; }
    public DateTime ViolatedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
