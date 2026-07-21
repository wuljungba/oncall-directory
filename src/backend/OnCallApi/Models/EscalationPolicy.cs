namespace OnCallApi.Models;

/// <summary>
/// Defines an escalation policy for a department.
/// Determines when and how to escalate when no one responds.
/// </summary>
public class EscalationPolicy
{
    public int Id { get; set; }
    public int? DepartmentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int MaxResponseMinutes { get; set; } = 15;
    public int EscalationTierCount { get; set; } = 3;
    public string NotificationChannels { get; set; } = "teams,email";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Department? Department { get; set; }
}

/// <summary>
/// Records an escalation event — fired when a shift holder doesn't respond in time.
/// </summary>
public class EscalationEvent
{
    public int Id { get; set; }
    public int PolicyId { get; set; }
    public Guid EmployeeId { get; set; }
    public int ShiftId { get; set; }
    public int Tier { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime TriggeredAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public string Details { get; set; } = string.Empty;

    public EscalationPolicy? Policy { get; set; }
    public Employee? Employee { get; set; }
    public Shift? Shift { get; set; }
}
