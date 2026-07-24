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
    List<string>? Languages
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
    bool? IsActive
);

/// <summary>Request to create a new department (sub-account).</summary>
public record CreateDepartmentRequest(
    string Name,
    string? Description,
    string? AzureAdGroupId
);

/// <summary>Request to update a department.</summary>
public record UpdateDepartmentRequest(
    string Name,
    string? Description,
    bool? IsActive
);
