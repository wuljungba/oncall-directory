namespace OnCallApi.Models;

/// <summary>E.164 phone number regex: +{country code}{national number}, max 15 digits.</summary>
public static class PhoneFormats
{
    public const string E164Pattern = @"^\+[1-9]\d{1,14}$";
    public const string E164DisplayName = "E.164 format (+ followed by 2-15 digits, e.g. +1234567890)";
}

/// <summary>The kinds of row the directory holds. See <see cref="Employee.ContactType"/>.</summary>
public static class ContactType
{
    /// <summary>A human being. Has a name, usually an email, and may sign in.</summary>
    public const string Person = "Person";

    /// <summary>A unit, floor or service line reached by phone. Never a security principal.</summary>
    public const string Department = "Department";

    public static bool IsKnown(string? value) => value is Person or Department;
}

public class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AzureAdObjectId { get; set; } = string.Empty;

    /// <summary>
    /// Kept non-nullable and defaulted to empty rather than made nullable: sixty-odd
    /// call sites build a name with $"{FirstName} {LastName}", and a null would turn
    /// each into a silent empty string anyway while adding a dereference risk to the
    /// two that call .ToLower(). A <see cref="ContactType"/> of "Department" simply
    /// leaves both blank and carries its label in <see cref="DisplayName"/>.
    /// Emptiness is enforced conditionally by EmployeeValidator, not by an attribute.
    /// </summary>
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// What to show when there is no person's name: the unit or service label of a
    /// "Department" contact ("3North"). Null for an ordinary person, whose display
    /// name is built from first and last name as it always was.
    /// </summary>
    [MaxLength(200)]
    public string? DisplayName { get; set; }

    [MaxLength(200)]
    public string? Title { get; set; }

    /// <summary>
    /// Post-nominal letters -- "MD", "RN, BSN" -- kept apart from the name.
    ///
    /// They arrive attached to it ("Jane Smith, MD") and used to be stored that way, which
    /// made the surname "Smith, MD" and put the person out of reach of a search for
    /// "Smith".
    /// </summary>
    [MaxLength(100)]
    public string? Credentials { get; set; }

    [MaxLength(200)]
    public string? Specialty { get; set; }

    [MaxLength(100)]
    public string? ClinicalRole { get; set; }

    /// <summary>
    /// Optional. A department/unit contact ("3North", x3434) has no mailbox, and
    /// requiring one made such a contact impossible to store at all.
    ///
    /// The unique index on this column is FILTERED to non-null rows (see AppDbContext):
    /// without that filter the second email-less contact collides with the first.
    /// Anything matching an employee BY this column must treat null as "no match" --
    /// two absent emails are not the same person.
    /// </summary>
    [EmailAddress]
    [MaxLength(256)]
    public string? Email { get; set; }

    [Phone]
    [MaxLength(50)]
    [RegularExpression(PhoneFormats.E164Pattern, ErrorMessage = "OfficePhone must be in E.164 format (+ followed by 2-15 digits, e.g. +1234567890)")]
    public string? OfficePhone { get; set; }

    [Phone]
    [MaxLength(50)]
    [RegularExpression(PhoneFormats.E164Pattern, ErrorMessage = "MobilePhone must be in E.164 format (+ followed by 2-15 digits, e.g. +1234567890)")]
    public string? MobilePhone { get; set; }

    [MaxLength(50)]
    [RegularExpression(PhoneFormats.E164Pattern, ErrorMessage = "PagerNumber must be in E.164 format (+ followed by 2-15 digits, e.g. +1234567890)")]
    public string? PagerNumber { get; set; }

    /// <summary>
    /// The internal extension, digits only, held separately from the dialable number.
    ///
    /// It is NOT folded into OfficePhone: PhoneValidation.NormalizeToDialable refuses a
    /// number carrying an extension precisely because stripping the punctuation turned
    /// "202-555-0134 x4412" into "+120255501344412", which passes E.164 and would have
    /// been dialled. The extension lives here; the number it hangs off lives in
    /// OfficePhone, derived from the tenant's dial-plan prefix when the file gave only
    /// an extension.
    /// </summary>
    [MaxLength(16)]
    public string? Extension { get; set; }

    /// <summary>
    /// "Person" (default) or "Department". A "Department" row is a unit or service line
    /// reached by phone -- it has a <see cref="DisplayName"/> and a number, and no name,
    /// email or sign-in identity. It is never a security principal: nothing may resolve
    /// a permission grant, local account or token to a "Department" row.
    /// </summary>
    [MaxLength(20)]
    public string ContactType { get; set; } = Models.ContactType.Person;

    [MaxLength(200)]
    public string? OfficeLocation { get; set; }
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }

    /// <summary>The tenant/business this employee belongs to.</summary>
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public Guid? ManagerId { get; set; }
    public Employee? Manager { get; set; }
    public string? Certifications { get; set; } // JSON array
    public string? Languages { get; set; } // JSON array
    public bool OnCallStatus { get; set; }
    public string Presence { get; set; } = "unknown";
    public bool IsActive { get; set; } = true;
    public DateTime LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Record origin (see docs/onboarding-standard.md). Standard values:
    /// "Ad" (created/confirmed by the Azure AD sync — the only source eligible for
    /// AD-driven deactivation), "CsvImport" (bulk CSV import), "Local" (manually
    /// created, and the safe default for any path that doesn't tag an origin).
    /// Any non-"Ad" record is locally-managed and the AD sync must NEVER
    /// deactivate it, regardless of its <see cref="AzureAdObjectId"/>.
    /// </summary>
    [MaxLength(64)]
    public string Source { get; set; } = "Local";

    // Navigation properties
    public ICollection<Employee> DirectReports { get; set; } = new List<Employee>();
    public ICollection<Shift> Shifts { get; set; } = new List<Shift>();
    public ICollection<ShiftSwap> SwapRequests { get; set; } = new List<ShiftSwap>();
}
