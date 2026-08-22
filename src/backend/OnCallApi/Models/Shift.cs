namespace OnCallApi.Models;

public class Shift
{
    public int Id { get; set; }
    public int ScheduleId { get; set; }
    public Schedule Schedule { get; set; } = null!;
    public Guid EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    [Required(AllowEmptyStrings = false)]
    [MaxLength(20)]
    public string Tier { get; set; } = "primary"; // primary, secondary, tertiary

    [Required(AllowEmptyStrings = false)]
    [MaxLength(20)]
    public string Status { get; set; } = "scheduled"; // scheduled, swapped, covered, gap
    public string? Notes { get; set; }

    /// <summary>
    /// When the on-call holder confirmed they are covering this shift.
    ///
    /// Escalation depends on this. Without it the engine had no notion of a response at
    /// all and simply fired once a shift had been running longer than the policy window —
    /// meaning every shift escalated, every tier, forever.
    /// </summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>Who acknowledged — normally the shift holder, but an admin may cover for them.</summary>
    public Guid? AcknowledgedById { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
