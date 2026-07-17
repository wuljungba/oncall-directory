using OnCallApi.Models;

namespace OnCallApi.Services;

public interface IDirectoryService
{
    Task<List<Employee>> SearchEmployeesAsync(string query, int? departmentId = null);
    Task<List<Employee>> GetDepartmentEmployeesAsync(int departmentId);
    Task<Employee?> GetEmployeeByIdAsync(Guid id);
    Task<Employee?> GetEmployeeByEmailAsync(string email);
    Task<List<PhoneTree>> GetPhoneTreesAsync(int? departmentId = null);
    Task<PhoneTree?> GetPhoneTreeByIdAsync(int id);
    Task<List<Employee>> GetOnCallEmployeesAsync(int? departmentId = null);
    Task UpdatePresenceAsync(Guid employeeId, string presence);
}
