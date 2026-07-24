using System.ComponentModel.DataAnnotations;

namespace OnCallApi.Models;

/// <summary>Request DTO for assigning a shift to an employee.</summary>
public record AssignShiftRequest(
    [Required] Guid EmployeeId,
    [Required] DateTime StartTime,
    [Required] DateTime EndTime,
    [MaxLength(20)] string Tier = "primary"
);

/// <summary>Request DTO for requesting a shift swap.</summary>
public record SwapRequest(
    [Required] int ShiftId,
    [Required] Guid RequestedById,
    Guid? ReplacementUserId,
    [MaxLength(500)] string? Reason
);
