using System.ComponentModel.DataAnnotations;

namespace OnCallApi.Models;

/// <summary>
/// Someone asking to be let in.
///
/// The app is invite-only: signing in with Microsoft or Google proves who you are and
/// grants nothing, and an admin then provisions you against a tenant. That is the right
/// shape for a system where tenant isolation is the security boundary, but it left a new
/// person at a dead end with no way to ask. This is the ask.
///
/// Approving one is deliberately NOT a grant of access. It records that an admin has
/// triaged the request; the permissions themselves are still assigned by hand, scoped to
/// a tenant, on the Permissions screen. Nothing here can widen anyone's access on its own.
/// </summary>
public class AccessRequest
{
    public int Id { get; set; }

    [Required]
    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? FullName { get; set; }

    [MaxLength(200)]
    public string? Organization { get; set; }

    /// <summary>What they say they do — free text, for the admin to judge, never trusted.</summary>
    [MaxLength(200)]
    public string? RoleRequested { get; set; }

    [MaxLength(1000)]
    public string? Note { get; set; }

    /// <summary>pending | approved | denied</summary>
    [MaxLength(20)]
    public string Status { get; set; } = AccessRequestStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ReviewedAt { get; set; }

    [MaxLength(200)]
    public string? ReviewedByName { get; set; }

    [MaxLength(1000)]
    public string? ReviewNote { get; set; }
}

public static class AccessRequestStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";

    public static bool IsKnown(string? status) =>
        status is Pending or Approved or Denied;
}

/// <summary>Body of the anonymous submission. Every field is caller-supplied and untrusted.</summary>
public record SubmitAccessRequest(
    string? Email,
    string? FullName,
    string? Organization,
    string? RoleRequested,
    string? Note);

/// <summary>Body of an admin's decision.</summary>
public record ReviewAccessRequestBody(string? Note);
