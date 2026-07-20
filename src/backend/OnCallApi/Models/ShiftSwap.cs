namespace OnCallApi.Models;

public class ShiftSwap
{
    public int Id { get; set; }
    public int OriginalShiftId { get; set; }
    public Shift OriginalShift { get; set; } = null!;
    public Guid RequestedById { get; set; }
    public Employee RequestedBy { get; set; } = null!;
    public Guid? ReplacementUserId { get; set; }
    public Employee? ReplacementUser { get; set; }
    [Required(AllowEmptyStrings = false)]
    [MaxLength(20)]
    public string Status { get; set; } = "pending"; // pending, approved, rejected, cancelled
    public string? Reason { get; set; }
    public Guid? ApprovedById { get; set; }
    public Employee? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
