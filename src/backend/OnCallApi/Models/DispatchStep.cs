using System.ComponentModel.DataAnnotations;

namespace OnCallApi.Models;

/// <summary>
/// Tracks the progress of a code call through its dispatch pipeline steps.
/// Each incident progresses through: created → cucm_check → informacast → vocera → acknowledged
/// </summary>
public class DispatchStep
{
    public int Id { get; set; }

    public int PhoneTreeEventId { get; set; }
    public PhoneTreeEvent PhoneTreeEvent { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string StepKey { get; set; } = string.Empty; // created, cucm_check, informacast, vocera, sip_fallback, acknowledged

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "pending"; // pending, completed, failed, skipped

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    [MaxLength(1000)]
    public string? Detail { get; set; }
}
