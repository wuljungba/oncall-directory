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
    Task<List<Employee>> GetDirectReportsAsync(Guid managerId);

    // ── Departments (Sub-accounts) ──
    Task<List<Department>> GetAllDepartmentsAsync(bool includeInactive = false);
    Task<Department> CreateDepartmentAsync(CreateDepartmentRequest request);
    Task<Department> UpdateDepartmentAsync(int id, UpdateDepartmentRequest request);
    Task DeactivateDepartmentAsync(int id);
    Task<List<Employee>> GetDepartmentMembersAsync(int departmentId);
}
