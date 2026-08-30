using System.ComponentModel.DataAnnotations;

namespace OnCallApi.Models;

/// <summary>
/// Request to create a new employee account.
///
/// The annotations live on this record, not only on the Employee entity. [ApiController]
/// validates the bound request type, so entity-level attributes never fired on this path
/// and the API happily accepted "not-an-email" and 300-character names against a
/// MaxLength(100). An EmployeeValidator exists and is registered in DI, but nothing ever
/// invokes it and it targets the entity rather than this record -- so it was dead code.
/// Same fix already applied to the tenant requests below.
///
/// Phones are deliberately NOT constrained to E.164 here: AdminService normalizes them the
/// way the importer does, so "(202) 555-0134" is accepted and stored canonically rather
/// than rejected for formatting.
/// </summary>
public record CreateEmployeeRequest(
    [MaxLength(100)]
    string? AzureAdObjectId,

    [Required(AllowEmptyStrings = false), MaxLength(100)]
    string FirstName,

    [Required(AllowEmptyStrings = false), MaxLength(100)]
    string LastName,

    [Required(AllowEmptyStrings = false), EmailAddress, MaxLength(256)]
    string Email,

    [MaxLength(200)] string? Title,
    [MaxLength(200)] string? Specialty,
    [MaxLength(100)] string? ClinicalRole,
    [MaxLength(50)] string? OfficePhone,
    [MaxLength(50)] string? MobilePhone,
    [MaxLength(50)] string? PagerNumber,
    [MaxLength(200)] string? OfficeLocation,
    int? DepartmentId,
    Guid? ManagerId,
    List<string>? Certifications,
    List<string>? Languages,
    int? TenantId = null
);

/// <summary>Request to update an existing employee account. See CreateEmployeeRequest.</summary>
public record UpdateEmployeeRequest(
    [Required(AllowEmptyStrings = false), MaxLength(100)]
    string FirstName,

    [Required(AllowEmptyStrings = false), MaxLength(100)]
    string LastName,

    [Required(AllowEmptyStrings = false), EmailAddress, MaxLength(256)]
    string Email,

    [MaxLength(200)] string? Title,
    [MaxLength(200)] string? Specialty,
    [MaxLength(100)] string? ClinicalRole,
    [MaxLength(50)] string? OfficePhone,
    [MaxLength(50)] string? MobilePhone,
    [MaxLength(50)] string? PagerNumber,
    [MaxLength(200)] string? OfficeLocation,
    int? DepartmentId,
    Guid? ManagerId,
    List<string>? Certifications,
    List<string>? Languages,
    bool? IsActive,
    int? TenantId = null
);

/// <summary>Request to create a new department (sub-account).</summary>
public record CreateDepartmentRequest(
    string Name,
    string? Description,
    string? Category,
    string? AzureAdGroupId,
    int? TenantId = null
);

/// <summary>Request to update a department.</summary>
public record UpdateDepartmentRequest(
    string Name,
    string? Description,
    string? Category,
    bool? IsActive,
    int? TenantId = null
);

// ── Tenant Requests ──

/// <summary>
/// Request to create a new tenant (business/facility).
///
/// The annotations live here, not only on the Tenant entity: [ApiController] validates the
/// bound request type, so an entity-only [Required] never fired and an empty or oversized
/// name reached SaveChanges — a 500 on SQL Server, or silently stored on SQLite.
/// </summary>
public record CreateTenantRequest(
    [Required(AllowEmptyStrings = false)]
    [MaxLength(200)]
    string Name,

    [MaxLength(1000)]
    string? Description,

    [MaxLength(100)]
    string? AzureAdGroupId,

    /// <summary>
    /// Entra tenant GUID whose users may read this subscription. Setting it is the act of
    /// approval, so it is validated as a GUID rather than accepted as free text: a typo
    /// here either silently connects nobody or, matched against another row, the wrong
    /// directory.
    /// </summary>
    [RegularExpression(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        ErrorMessage = "Directory tenant ID must be an Entra tenant GUID.")]
    [MaxLength(100)]
    string? AzureAdTenantId,

    [EmailAddress]
    [MaxLength(100)]
    string? ContactEmail
);

/// <summary>Request to update a tenant. Every field is optional; omitted ones are unchanged.</summary>
public record UpdateTenantRequest(
    [MaxLength(200)]
    string? Name,

    [MaxLength(1000)]
    string? Description,

    [MaxLength(100)]
    string? AzureAdGroupId,

    /// <summary>
    /// Entra tenant GUID whose users may read this subscription. Setting it is the act of
    /// approval, so it is validated as a GUID rather than accepted as free text: a typo
    /// here either silently connects nobody or, matched against another row, the wrong
    /// directory.
    /// </summary>
    [RegularExpression(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        ErrorMessage = "Directory tenant ID must be an Entra tenant GUID.")]
    [MaxLength(100)]
    string? AzureAdTenantId,

    [EmailAddress]
    [MaxLength(100)]
    string? ContactEmail,

    bool? IsActive
);

/// <summary>Request to assign a user as a tenant admin.</summary>
public record AssignTenantAdminRequest(
    string AzureAdObjectId,
    string Role
);

/// <summary>Request to update a tenant admin's role.</summary>
public record UpdateTenantAdminRequest(
    string? Role
);
