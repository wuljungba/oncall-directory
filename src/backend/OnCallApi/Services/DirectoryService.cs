using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

public class DirectoryService : IDirectoryService
{
    private readonly AppDbContext _db;

    public DirectoryService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<Employee>> SearchEmployeesAsync(string query, int? departmentId = null)
    {
        var q = _db.Employees
            .Include(e => e.Department)
            .Where(e => e.IsActive);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var search = query.ToLower();
            q = q.Where(e =>
                e.FirstName.ToLower().Contains(search) ||
                e.LastName.ToLower().Contains(search) ||
                (e.Title != null && e.Title.ToLower().Contains(search)) ||
                e.Email.ToLower().Contains(search));
        }

        if (departmentId.HasValue)
            q = q.Where(e => e.DepartmentId == departmentId.Value);

        return await q.OrderBy(e => e.LastName).ThenBy(e => e.FirstName).ToListAsync();
    }

    public async Task<List<Employee>> GetDepartmentEmployeesAsync(int departmentId)
    {
        return await _db.Employees
            .Include(e => e.Department)
            .Where(e => e.DepartmentId == departmentId && e.IsActive)
            .OrderBy(e => e.LastName)
            .ToListAsync();
    }

    public async Task<Employee?> GetEmployeeByIdAsync(Guid id)
    {
        return await _db.Employees
            .Include(e => e.Department)
            .Include(e => e.Manager)
            .FirstOrDefaultAsync(e => e.Id == id);
    }

    public async Task<Employee?> GetEmployeeByEmailAsync(string email)
    {
        return await _db.Employees
            .Include(e => e.Department)
            .FirstOrDefaultAsync(e => e.Email.ToLower() == email.ToLower());
    }

    public async Task<List<PhoneTree>> GetPhoneTreesAsync(int? departmentId = null)
    {
        var query = _db.PhoneTrees
            .Include(t => t.Nodes.OrderBy(n => n.Order))
            .ThenInclude(n => n.Employee)
            .AsQueryable();

        if (departmentId.HasValue)
            query = query.Where(t => t.DepartmentId == departmentId.Value);

        return await query.Where(t => t.IsActive).ToListAsync();
    }

    public async Task<PhoneTree?> GetPhoneTreeByIdAsync(int id)
    {
        return await _db.PhoneTrees
            .Include(t => t.Nodes.OrderBy(n => n.Order))
            .ThenInclude(n => n.Employee)
            .FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task<List<Employee>> GetOnCallEmployeesAsync(int? departmentId = null)
    {
        var now = DateTime.UtcNow;
        var query = _db.Employees
            .Include(e => e.Department)
            .Where(e => e.OnCallStatus && e.IsActive);

        if (departmentId.HasValue)
            query = query.Where(e => e.DepartmentId == departmentId.Value);

        return await query.ToListAsync();
    }

    public async Task UpdatePresenceAsync(Guid employeeId, string presence)
    {
        var employee = await _db.Employees.FindAsync(employeeId);
        if (employee != null)
        {
            employee.Presence = presence;
            employee.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }
}
