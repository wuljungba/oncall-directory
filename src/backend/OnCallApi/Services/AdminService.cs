using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

public class AdminService : IAdminService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AdminService> _logger;

    public AdminService(AppDbContext db, ILogger<AdminService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Employees ──

    public async Task<List<Employee>> GetAllEmployeesAsync(bool includeInactive = false)
    {
        var query = _db.Employees
            .Include(e => e.Department)
            .Include(e => e.Manager)
            .AsQueryable();

        if (!includeInactive)
            query = query.Where(e => e.IsActive);

        return await query.OrderBy(e => e.LastName).ThenBy(e => e.FirstName).ToListAsync();
    }

    public async Task<Employee?> GetEmployeeByIdAsync(Guid id)
    {
        return await _db.Employees
            .Include(e => e.Department)
            .Include(e => e.Manager)
            .Include(e => e.DirectReports)
            .FirstOrDefaultAsync(e => e.Id == id);
    }

    public async Task<Employee> CreateEmployeeAsync(CreateEmployeeRequest request)
    {
        var employee = new Employee
        {
            Id = Guid.NewGuid(),
            AzureAdObjectId = request.AzureAdObjectId ?? string.Empty,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            Title = request.Title,
            Specialty = request.Specialty,
            ClinicalRole = request.ClinicalRole,
            OfficePhone = request.OfficePhone,
            MobilePhone = request.MobilePhone,
            PagerNumber = request.PagerNumber,
            OfficeLocation = request.OfficeLocation,
            DepartmentId = request.DepartmentId,
            ManagerId = request.ManagerId,
            Certifications = JsonSerializer.Serialize(request.Certifications ?? new List<string>()),
            Languages = JsonSerializer.Serialize(request.Languages ?? new List<string>()),
            IsActive = true,
            Presence = "unknown",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.Employees.Add(employee);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_Employees_Email") == true)
        {
            throw new InvalidOperationException($"An employee with email '{request.Email}' already exists.");
        }

        // Reload with navigation properties
        return (await _db.Employees
            .Include(e => e.Department)
            .Include(e => e.Manager)
            .FirstAsync(e => e.Id == employee.Id))!;
    }

    public async Task<Employee> UpdateEmployeeAsync(Guid id, UpdateEmployeeRequest request)
    {
        var existing = await _db.Employees.FindAsync(id)
            ?? throw new KeyNotFoundException($"Employee {id} not found.");

        // Prevent circular manager reference
        if (request.ManagerId.HasValue && request.ManagerId.Value == id)
            throw new InvalidOperationException("An employee cannot be their own manager.");

        if (request.ManagerId.HasValue)
        {
            var wouldCycle = await WouldCreateManagerCycleAsync(id, request.ManagerId.Value);
            if (wouldCycle)
                throw new InvalidOperationException("This assignment would create a circular manager reference.");
        }

        existing.FirstName = request.FirstName;
        existing.LastName = request.LastName;
        existing.Email = request.Email;
        existing.Title = request.Title;
        existing.Specialty = request.Specialty;
        existing.ClinicalRole = request.ClinicalRole;
        existing.OfficePhone = request.OfficePhone;
        existing.MobilePhone = request.MobilePhone;
        existing.PagerNumber = request.PagerNumber;
        existing.OfficeLocation = request.OfficeLocation;
        existing.DepartmentId = request.DepartmentId;
        existing.ManagerId = request.ManagerId;
        if (request.Certifications != null)
            existing.Certifications = JsonSerializer.Serialize(request.Certifications);
        if (request.Languages != null)
            existing.Languages = JsonSerializer.Serialize(request.Languages);
        if (request.IsActive.HasValue)
            existing.IsActive = request.IsActive.Value;
        existing.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_Employees_Email") == true)
        {
            throw new InvalidOperationException($"An employee with email '{request.Email}' already exists.");
        }

        return (await _db.Employees
            .Include(e => e.Department)
            .Include(e => e.Manager)
            .FirstAsync(e => e.Id == id))!;
    }

    public async Task DeactivateEmployeeAsync(Guid id)
    {
        var employee = await _db.Employees.FindAsync(id)
            ?? throw new KeyNotFoundException($"Employee {id} not found.");

        employee.IsActive = false;
        employee.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task ReactivateEmployeeAsync(Guid id)
    {
        var employee = await _db.Employees.FindAsync(id)
            ?? throw new KeyNotFoundException($"Employee {id} not found.");

        employee.IsActive = true;
        employee.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<List<Employee>> GetDirectReportsAsync(Guid managerId)
    {
        return await _db.Employees
            .Include(e => e.Department)
            .Where(e => e.ManagerId == managerId && e.IsActive)
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .ToListAsync();
    }

    // ── Departments ──

    public async Task<List<Department>> GetAllDepartmentsAsync(bool includeInactive = false)
    {
        var query = _db.Departments.AsQueryable();
        if (!includeInactive)
            query = query.Where(d => d.IsActive);
        return await query.OrderBy(d => d.Name).ToListAsync();
    }

    public async Task<Department> CreateDepartmentAsync(CreateDepartmentRequest request)
    {
        var department = new Department
        {
            Name = request.Name,
            Description = request.Description,
            AzureAdGroupId = request.AzureAdGroupId,
            IsActive = true,
        };

        _db.Departments.Add(department);
        await _db.SaveChangesAsync();
        return department;
    }

    public async Task<Department> UpdateDepartmentAsync(int id, UpdateDepartmentRequest request)
    {
        var existing = await _db.Departments.FindAsync(id)
            ?? throw new KeyNotFoundException($"Department {id} not found.");

        existing.Name = request.Name;
        existing.Description = request.Description ?? existing.Description;
        if (request.IsActive.HasValue)
            existing.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task DeactivateDepartmentAsync(int id)
    {
        var department = await _db.Departments.FindAsync(id)
            ?? throw new KeyNotFoundException($"Department {id} not found.");

        department.IsActive = false;
        await _db.SaveChangesAsync();
    }

    public async Task<List<Employee>> GetDepartmentMembersAsync(int departmentId)
    {
        return await _db.Employees
            .Include(e => e.Department)
            .Where(e => e.DepartmentId == departmentId && e.IsActive)
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .ToListAsync();
    }

    // ── Helpers ──

    /// <summary>
    /// Checks if setting potentialManagerId as the manager of employeeId
    /// would create a circular reference (potentialManager is already a
    /// subordinate of employeeId somewhere in the chain).
    /// </summary>
    private async Task<bool> WouldCreateManagerCycleAsync(Guid employeeId, Guid potentialManagerId)
    {
        var visited = new HashSet<Guid> { employeeId };
        var current = potentialManagerId;

        while (true)
        {
            if (visited.Contains(current))
                return true;

            visited.Add(current);

            var manager = await _db.Employees
                .Where(e => e.Id == current)
                .Select(e => e.ManagerId)
                .FirstOrDefaultAsync();

            if (manager == null)
                return false;

            current = manager.Value;
        }
    }
}
