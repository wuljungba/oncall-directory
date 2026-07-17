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
    public string Tier { get; set; } = "primary"; // primary, secondary, tertiary
    public string Status { get; set; } = "scheduled"; // scheduled, swapped, covered, gap
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
