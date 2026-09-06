using OnCallApi.Models;

namespace OnCallApi.Services;

public interface IAdminService
{
    // ── Employees (Accounts) ──
    Task<List<Employee>> GetAllEmployeesAsync(bool includeInactive = false);
    Task<Employee?> GetEmployeeByIdAsync(Guid id);
    Task<Employee> CreateEmployeeAsync(CreateEmployeeRequest request);
    Task<Employee> UpdateEmployeeAsync(Guid id, UpdateEmployeeRequest request);
    Task DeactivateEmployeeAsync(Guid id);
    Task ReactivateEmployeeAsync(Guid id);
    Task DeleteEmployeeAsync(Guid id);

    // Bulk equivalents. They report per record rather than succeeding or failing as a whole,
    // because history foreign keys make a part-blocked batch the ordinary result, and they
    // revoke the permission grants that would otherwise outlive the person.
    Task<BulkEmployeeActionResponse> BulkDeactivateEmployeesAsync(
        IReadOnlyList<Guid> employeeIds, string? reason, bool acknowledgePrivileged, CancellationToken ct = default);
    Task<BulkEmployeeActionResponse> BulkReactivateEmployeesAsync(
        IReadOnlyList<Guid> employeeIds, string? reason, bool acknowledgePrivileged, CancellationToken ct = default);
    Task<BulkEmployeeActionResponse> BulkDeleteEmployeesAsync(
        IReadOnlyList<Guid> employeeIds, string? reason, bool acknowledgePrivileged, CancellationToken ct = default);
    Task<List<Employee>> GetDirectReportsAsync(Guid managerId);

    // ── Departments (Sub-accounts) ──
    Task<List<Department>> GetAllDepartmentsAsync(bool includeInactive = false);
    Task<Department> CreateDepartmentAsync(CreateDepartmentRequest request);
    Task<Department> UpdateDepartmentAsync(int id, UpdateDepartmentRequest request);
    Task DeactivateDepartmentAsync(int id);
    Task<List<Employee>> GetDepartmentMembersAsync(int departmentId);
}
