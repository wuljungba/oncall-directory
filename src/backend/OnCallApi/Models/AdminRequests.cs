namespace OnCallApi.Models;

/// <summary>Request to create a new employee account.</summary>
public record CreateEmployeeRequest(
    string? AzureAdObjectId,
    string FirstName,
    string LastName,
    string Email,
    string? Title,
    string? Specialty,
    string? ClinicalRole,
    string? OfficePhone,
    string? MobilePhone,
    string? PagerNumber,
    string? OfficeLocation,
    int? DepartmentId,
    Guid? ManagerId,
    List<string>? Certifications,
    List<string>? Languages,
    int? TenantId = null
);

/// <summary>Request to update an existing employee account.</summary>
public record UpdateEmployeeRequest(
    string FirstName,
    string LastName,
    string Email,
    string? Title,
    string? Specialty,
    string? ClinicalRole,
    string? OfficePhone,
    string? MobilePhone,
    string? PagerNumber,
    string? OfficeLocation,
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

/// <summary>Request to create a new tenant (business/facility).</summary>
public record CreateTenantRequest(
    string Name,
    string? Description,
    string? AzureAdGroupId,
    string? ContactEmail
);

/// <summary>Request to update a tenant.</summary>
public record UpdateTenantRequest(
    string? Name,
    string? Description,
    string? AzureAdGroupId,
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
