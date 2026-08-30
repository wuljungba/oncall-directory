using System.ComponentModel.DataAnnotations;

namespace OnCallApi.Models;

/// <summary>Request DTO for resolving a phone tree event.</summary>
public record ResolveEventRequest(string? Outcome, string? NotifiedByName = null);

/// <summary>Request DTO for appending one entry to an incident's debrief log.</summary>
public record AddDebriefNoteRequest(string? Note);

/// <summary>
/// Request DTO for starting a code call (dispatch). <see cref="Confirm"/> must be true —
/// an explicit operator confirmation that they intend to fire a live broadcast. This is
/// the server-side consent gate: a raw client POST without confirmation is rejected.
/// </summary>
public class StartCodeCallRequest
{
    public DateTime? StartedAt { get; set; }
    public string? Location { get; set; }
    public string? LocationZone { get; set; }
    public string? Notes { get; set; }
    public string? RequestedByName { get; set; }

    /// <summary>Operator confirmation that a live code call should be dispatched.</summary>
    public bool Confirm { get; set; }
}

public class PhoneTreeEvent
{
    public int Id { get; set; }

    public int PhoneTreeId { get; set; }
    public PhoneTree? PhoneTree { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? EndedAt { get; set; }

    public DateTime? AcknowledgedAt { get; set; }

    public Guid? InitiatedById { get; set; }
    public Employee? InitiatedBy { get; set; }

    /// <summary>Name of the signed-in account/operator who triggered the code call. Pinned from
    /// the authenticated user (reliable across providers where Employee.Id resolution may differ).</summary>
    [MaxLength(200)]
    public string? InitiatedByName { get; set; }

    /// <summary>Free-text name of the person who called in / ordered the code (the reporter).</summary>
    [MaxLength(200)]
    public string? RequestedByName { get; set; }

    /// <summary>Free-text name of the person(s) notified after dispatch completed.</summary>
    [MaxLength(500)]
    public string? NotifiedByName { get; set; }

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

    /// <summary>
    /// The original single-field debrief note, kept only so incidents written before the
    /// debrief log existed still show what was recorded. Nothing writes it any more —
    /// new entries go to <see cref="DebriefLog"/>.
    /// </summary>
    [MaxLength(1000)]
    public string? DebriefNotes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PhoneTreeEventParticipant> Participants { get; set; } = new List<PhoneTreeEventParticipant>();
    public ICollection<DispatchStep> DispatchSteps { get; set; } = new List<DispatchStep>();
    public ICollection<DebriefNote> DebriefLog { get; set; } = new List<DebriefNote>();
}

/// <summary>
/// One entry in an incident's debrief log.
///
/// Append-only by design. The debrief is the written record of what happened during a
/// code call — who was reached, what went wrong, what changed afterwards — so an entry,
/// once saved, is never edited or removed. Correcting something means adding a further
/// entry that says so, which leaves both the original account and the correction in the
/// record. There is deliberately no update or delete path to this table.
///
/// This replaced a single overwritable string on the event, where saving a new note
/// silently destroyed the previous one.
/// </summary>
public class DebriefNote
{
    public int Id { get; set; }

    public int PhoneTreeEventId { get; set; }
    public PhoneTreeEvent? PhoneTreeEvent { get; set; }

    [Required]
    [MaxLength(2000)]
    public string Note { get; set; } = string.Empty;

    /// <summary>Who wrote it, from the signed-in identity — not caller-supplied.</summary>
    [MaxLength(200)]
    public string? AuthorName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PhoneTreeEventParticipant
{
    public int Id { get; set; }

    public int PhoneTreeEventId { get; set; }
    public PhoneTreeEvent? PhoneTreeEvent { get; set; }

    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    [MaxLength(50)]
    public string? Role { get; set; }

    public DateTime? RespondedAt { get; set; }

    public DateTime? AcknowledgedAt { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }
}
