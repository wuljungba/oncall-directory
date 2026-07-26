using System.ComponentModel.DataAnnotations;

namespace OnCallApi.Models;

/// <summary>Request DTO for resolving a phone tree event.</summary>
public record ResolveEventRequest(string? Outcome);

/// <summary>Request DTO for saving debrief notes.</summary>
public record SaveDebriefNotesRequest(string? Notes);

public class PhoneTreeEvent
{
    public int Id { get; set; }

    public int PhoneTreeId { get; set; }
    public PhoneTree PhoneTree { get; set; } = null!;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? EndedAt { get; set; }

    public DateTime? AcknowledgedAt { get; set; }

    public Guid? InitiatedById { get; set; }
    public Employee? InitiatedBy { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }

    [MaxLength(100)]
    public string? LocationZone { get; set; }

    [MaxLength(100)]
    public string? ExternalIncidentId { get; set; }

    public int? ResponseTimeSeconds { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = "active"; // active, completed

    [MaxLength(1000)]
    public string? Outcome { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    [MaxLength(1000)]
    public string? DebriefNotes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PhoneTreeEventParticipant> Participants { get; set; } = new List<PhoneTreeEventParticipant>();
    public ICollection<DispatchStep> DispatchSteps { get; set; } = new List<DispatchStep>();
}

public class PhoneTreeEventParticipant
{
    public int Id { get; set; }

    public int PhoneTreeEventId { get; set; }
    public PhoneTreeEvent PhoneTreeEvent { get; set; } = null!;

    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    [MaxLength(50)]
    public string? Role { get; set; }

    public DateTime? RespondedAt { get; set; }

    public DateTime? AcknowledgedAt { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }
}
