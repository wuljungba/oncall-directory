namespace OnCallApi.Models;

/// <summary>E.164 phone number regex: +{country code}{national number}, max 15 digits.</summary>
public static class PhoneFormats
{
    public const string E164Pattern = @"^\+[1-9]\d{1,14}$";
    public const string E164DisplayName = "E.164 format (+ followed by 2-15 digits, e.g. +1234567890)";
}

public class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AzureAdObjectId { get; set; } = string.Empty;
    [Required(AllowEmptyStrings = false)]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Title { get; set; }

    [MaxLength(200)]
    public string? Specialty { get; set; }

    [MaxLength(100)]
    public string? ClinicalRole { get; set; }

    [Required(AllowEmptyStrings = false)]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

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
