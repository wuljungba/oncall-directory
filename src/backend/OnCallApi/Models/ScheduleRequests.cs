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

/// <summary>Request DTO for updating a time-off entry.</summary>
public record TimeOffUpdateRequest(
    DateTime StartDate,
    DateTime EndDate,
    [MaxLength(20)] string Type,
    [MaxLength(500)] string? Notes
);

/// <summary>Request DTO for approving/denying time off.</summary>
public record TimeOffApprovalRequest(
    [MaxLength(500)] string? Reason
);
