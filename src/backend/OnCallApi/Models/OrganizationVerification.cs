using System.ComponentModel.DataAnnotations;

namespace OnCallApi.Models;

/// <summary>
/// What an organization claims to be, and what came back when it was checked.
///
/// A code call reaches real clinicians and a schedule says who is responsible for
/// patients tonight. Handing that to whoever filled in a signup form is the risk this
/// exists to reduce -- not by asking harder questions, but by checking one answer against
/// a public registry that the organization does not control.
///
/// None of these fields is PHI. They describe a business: its legal name, its NPI, its
/// address, and the person who says they may speak for it.
/// </summary>
public class OrganizationVerification
{
    public int Id { get; set; }

    public int TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    [Required(AllowEmptyStrings = false)]
    [MaxLength(300)]
    public string LegalName { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? DoingBusinessAs { get; set; }

    /// <summary>
    /// The organizational (Type 2) NPI. Ten digits, and public: it is the identifier the
    /// NPPES registry is keyed on, which is what makes this checkable at all.
    /// </summary>
    [MaxLength(10)]
    public string? Npi { get; set; }

    [MaxLength(300)]
    public string? AddressLine1 { get; set; }

    [MaxLength(120)]
    public string? City { get; set; }

    [MaxLength(60)]
    public string? State { get; set; }

    [MaxLength(20)]
    public string? PostalCode { get; set; }

    [MaxLength(100)]
    public string? StateLicenseNumber { get; set; }

    [MaxLength(60)]
    public string? LicenseState { get; set; }

    /// <summary>Optional. Not checked against anything; collected for the record.</summary>
    [MaxLength(20)]
    public string? Ein { get; set; }

    [MaxLength(200)]
    public string? RepresentativeName { get; set; }

    [MaxLength(200)]
    public string? RepresentativeTitle { get; set; }

    /// <summary>
    /// Must be on the organization's own domain. A gmail.com address for "St. Elsewhere
    /// Hospital" is the single cheapest signal that nobody has checked anything.
    /// </summary>
    [MaxLength(320)]
    public string? RepresentativeEmail { get; set; }

    [MaxLength(200)]
    public string? SubmittedByName { get; set; }

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    /// <summary>What the NPPES lookup said, in a sentence an admin can read.</summary>
    [MaxLength(2000)]
    public string? RegistryFindings { get; set; }

    public DateTime? RegistryCheckedAt { get; set; }

    public DateTime? DecidedAt { get; set; }

    [MaxLength(200)]
    public string? DecidedByName { get; set; }

    [MaxLength(1000)]
    public string? DecisionReason { get; set; }
}

/// <summary>
/// The kind of organization a subscription is. Only the healthcare kinds enter
/// verification: somebody managing a contact list has nothing to verify, and gating them
/// would block the frictionless import path for no gain in safety.
/// </summary>
public static class OrganizationType
{
    public const string Hospital = "Hospital";
    public const string Clinic = "Clinic";
    public const string PrivatePractice = "PrivatePractice";
    public const string SkilledNursing = "SkilledNursing";
    public const string Ems = "EMS";
    public const string Other = "Other";

    private static readonly string[] Healthcare =
        [Hospital, Clinic, PrivatePractice, SkilledNursing, Ems];

    public static bool IsKnown(string? value) =>
        value is Hospital or Clinic or PrivatePractice or SkilledNursing or Ems or Other;

    /// <summary>
    /// Whether declaring this type puts an organization into verification. "Other", and
    /// declaring nothing at all, do not.
    /// </summary>
    public static bool RequiresVerification(string? value) =>
        value != null && Healthcare.Contains(value, StringComparer.OrdinalIgnoreCase);
}

public static class VerificationStatus
{
    /// <summary>Declared a healthcare type and has not submitted anything yet.</summary>
    public const string Unverified = "Unverified";

    /// <summary>Submitted, and something did not match well enough to pass on its own.</summary>
    public const string Pending = "Pending";

    /// <summary>Checked out, or an administrator said so.</summary>
    public const string Verified = "Verified";

    public const string Rejected = "Rejected";

    public static bool IsKnown(string? value) =>
        value is Unverified or Pending or Verified or Rejected;
}

/// <summary>Body of a verification submission. Every field is caller-supplied and untrusted.</summary>
public record SubmitVerificationRequest(
    string? OrganizationType,
    string? LegalName,
    string? DoingBusinessAs,
    string? Npi,
    string? AddressLine1,
    string? City,
    string? State,
    string? PostalCode,
    string? StateLicenseNumber,
    string? LicenseState,
    string? Ein,
    string? RepresentativeName,
    string? RepresentativeTitle,
    string? RepresentativeEmail);

/// <summary>Body of an administrator's decision on a submission.</summary>
public record DecideVerificationRequest(string? Reason);
