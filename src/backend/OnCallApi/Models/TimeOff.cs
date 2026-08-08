namespace OnCallApi.Models;

public class TimeOff
{
    public int Id { get; set; }
    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    [Required(AllowEmptyStrings = false)]
    [MaxLength(20)]
    public string Type { get; set; } = "pto"; // pto, cme, holiday, sick

    [Required(AllowEmptyStrings = false)]
    [MaxLength(20)]
    public string Status { get; set; } = "pending"; // pending, approved, denied
    public string? Notes { get; set; }
    public Guid? ApprovedById { get; set; }
    public Employee? ApprovedBy { get; set; }
    [MaxLength(500)]
    public string? ApprovalReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
