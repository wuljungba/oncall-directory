using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

public class AdminService : IAdminService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AdminService> _logger;
    private readonly ITenantContextService _tenantContext;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AdminService(
        AppDbContext db,
        ILogger<AdminService> logger,
        ITenantContextService tenantContext,
        IHttpContextAccessor httpContextAccessor)
    {
        _db = db;
        _logger = logger;
        _tenantContext = tenantContext;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Returns the current user from the HTTP context.
    /// </summary>
    private ClaimsPrincipal? CurrentUser => _httpContextAccessor.HttpContext?.User;

    /// <summary>
    /// Applies tenant filtering to an employee query.
    /// Super admins see all employees; sub-admins only see employees in their authorized tenants.
    /// </summary>
    private async Task<IQueryable<Employee>> FilterEmployeesByTenant(IQueryable<Employee> query)
    {
        if (CurrentUser == null) return query;
        if (_tenantContext.IsSuperAdmin(CurrentUser)) return query;

        var tenantIds = await _tenantContext.GetAuthorizedTenantIdsAsync(CurrentUser);
        if (tenantIds.Count == 0) return query.Where(e => false); // No access

        return query.Where(e => e.TenantId.HasValue && tenantIds.Contains(e.TenantId.Value));
    }

    /// <summary>
    /// Applies tenant filtering to a department query.
    /// </summary>
    private async Task<IQueryable<Department>> FilterDepartmentsByTenant(IQueryable<Department> query)
    {
        if (CurrentUser == null) return query;
        if (_tenantContext.IsSuperAdmin(CurrentUser)) return query;

        var tenantIds = await _tenantContext.GetAuthorizedTenantIdsAsync(CurrentUser);
        if (tenantIds.Count == 0) return query.Where(d => false);

        return query.Where(d => d.TenantId.HasValue && tenantIds.Contains(d.TenantId.Value));
    }

    /// <summary>
    /// Gets the current user's tenant ID for auto-assignment on creates.
    /// For sub-admins, returns their first authorized tenant.
    /// For super admins, returns null (they can create across tenants).
    /// </summary>
    private async Task<int?> GetCurrentUserTenantId()
    {
        if (CurrentUser == null) return null;
        if (_tenantContext.IsSuperAdmin(CurrentUser)) return null;

        var tenantIds = await _tenantContext.GetAuthorizedTenantIdsAsync(CurrentUser);
        return tenantIds.FirstOrDefault();
    }

    /// <summary>
    /// Resolves the tenant for a create operation. A super admin may pick any
    /// tenant (or none); a sub-admin is always forced to their own tenant and any
    /// requested value is ignored — this keeps tenant isolation even through the
    /// admin GUI.
    /// </summary>
    private async Task<int?> ResolveCreateTenantId(int? requested)
    {
        if (CurrentUser != null && _tenantContext.IsSuperAdmin(CurrentUser))
        {
            if (requested.HasValue)
            {
                var exists = await _db.Tenants.AnyAsync(t => t.Id == requested.Value);
                if (!exists)
                    throw new InvalidOperationException($"Subscription (tenant) {requested.Value} does not exist.");
            }
            return requested;
        }
        return await GetCurrentUserTenantId();
    }

    // ── Employees ──

    public async Task<List<Employee>> GetAllEmployeesAsync(bool includeInactive = false)
    {
        var query = _db.Employees
            .Include(e => e.Department)
            .Include(e => e.Manager)
            .AsQueryable();

        query = await FilterEmployeesByTenant(query);

        if (!includeInactive)
            query = query.Where(e => e.IsActive);

        return await query.OrderBy(e => e.LastName).ThenBy(e => e.FirstName).ToListAsync();
    }

    public async Task<Employee?> GetEmployeeByIdAsync(Guid id)
    {
        IQueryable<Employee> query = _db.Employees
            .Include(e => e.Department)
            .Include(e => e.Manager)
            .Include(e => e.DirectReports);

        query = await FilterEmployeesByTenant(query);

        return await query.FirstOrDefaultAsync(e => e.Id == id);
    }

    public async Task<Employee> CreateEmployeeAsync(CreateEmployeeRequest request)
    {
        var tenantId = await ResolveCreateTenantId(request.TenantId);

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
            TenantId = tenantId, // Auto-assigned for sub-admins; null for super admins
            Certifications = JsonSerializer.Serialize(request.Certifications ?? new List<string>()),
            Languages = JsonSerializer.Serialize(request.Languages ?? new List<string>()),
            IsActive = true,
            Source = "Local",
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

        // Ensure sub-admin can only update employees in their tenant
        if (!_tenantContext.IsSuperAdmin(CurrentUser!))
        {
            var tenantIds = await _tenantContext.GetAuthorizedTenantIdsAsync(CurrentUser!);
            if (!existing.TenantId.HasValue || !tenantIds.Contains(existing.TenantId.Value))
                throw new KeyNotFoundException($"Employee {id} not found.");
        }

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
        // Super admin may move an employee between subscriptions.
        if (request.TenantId.HasValue && CurrentUser != null && _tenantContext.IsSuperAdmin(CurrentUser))
            existing.TenantId = request.TenantId.Value;
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

        // Ensure sub-admin can only deactivate employees in their tenant
        if (!_tenantContext.IsSuperAdmin(CurrentUser!))
        {
            var tenantIds = await _tenantContext.GetAuthorizedTenantIdsAsync(CurrentUser!);
            if (!employee.TenantId.HasValue || !tenantIds.Contains(employee.TenantId.Value))
                throw new KeyNotFoundException($"Employee {id} not found.");
        }

        employee.IsActive = false;
        employee.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task ReactivateEmployeeAsync(Guid id)
    {
        var employee = await _db.Employees.FindAsync(id)
            ?? throw new KeyNotFoundException($"Employee {id} not found.");

        // Ensure sub-admin can only reactivate employees in their tenant
        if (!_tenantContext.IsSuperAdmin(CurrentUser!))
        {
            var tenantIds = await _tenantContext.GetAuthorizedTenantIdsAsync(CurrentUser!);
            if (!employee.TenantId.HasValue || !tenantIds.Contains(employee.TenantId.Value))
                throw new KeyNotFoundException($"Employee {id} not found.");
        }

        employee.IsActive = true;
        employee.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<List<Employee>> GetDirectReportsAsync(Guid managerId)
    {
        var query = _db.Employees
            .Include(e => e.Department)
            .Where(e => e.ManagerId == managerId && e.IsActive);

        query = await FilterEmployeesByTenant(query);

        return await query
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .ToListAsync();
    }

    // ── Departments ──

    public async Task<List<Department>> GetAllDepartmentsAsync(bool includeInactive = false)
    {
        var query = _db.Departments.AsQueryable();

        query = await FilterDepartmentsByTenant(query);

        if (!includeInactive)
            query = query.Where(d => d.IsActive);
        return await query.OrderBy(d => d.Name).ToListAsync();
    }

    public async Task<Department> CreateDepartmentAsync(CreateDepartmentRequest request)
    {
        var tenantId = await ResolveCreateTenantId(request.TenantId);

        var department = new Department
        {
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            AzureAdGroupId = request.AzureAdGroupId,
            TenantId = tenantId, // Auto-assigned for sub-admins; null for super admins
            IsActive = true,
        };

        _db.Departments.Add(department);
        await _db.SaveChangesAsync();
        return department;
    }

    public async Task<Department> UpdateDepartmentAsync(int id, UpdateDepartmentRequest request)
    {
        var query = _db.Departments.AsQueryable();
        query = await FilterDepartmentsByTenant(query);

        var existing = await query.FirstOrDefaultAsync(d => d.Id == id)
            ?? throw new KeyNotFoundException($"Department {id} not found.");

        existing.Name = request.Name;
        existing.Description = request.Description ?? existing.Description;
        existing.Category = request.Category ?? existing.Category;
        if (request.IsActive.HasValue)
            existing.IsActive = request.IsActive.Value;
        // Super admin may move a department to another subscription.
        if (request.TenantId.HasValue && CurrentUser != null && _tenantContext.IsSuperAdmin(CurrentUser))
            existing.TenantId = request.TenantId.Value;

        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task DeactivateDepartmentAsync(int id)
    {
        var query = _db.Departments.AsQueryable();
        query = await FilterDepartmentsByTenant(query);

        var department = await query.FirstOrDefaultAsync(d => d.Id == id)
            ?? throw new KeyNotFoundException($"Department {id} not found.");

        department.IsActive = false;
        await _db.SaveChangesAsync();
    }

    public async Task<List<Employee>> GetDepartmentMembersAsync(int departmentId)
    {
        var deptQuery = _db.Departments.AsQueryable();
        deptQuery = await FilterDepartmentsByTenant(deptQuery);

        var deptExists = await deptQuery.AnyAsync(d => d.Id == departmentId);
        if (!deptExists) return [];

        var empQuery = _db.Employees
            .Include(e => e.Department)
            .Where(e => e.DepartmentId == departmentId && e.IsActive);

        empQuery = await FilterEmployeesByTenant(empQuery);

        return await empQuery
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .ToListAsync();
    }

    // ── Helpers ──

    /// <summary>
    /// Checks if setting potentialManagerId as the manager of employeeId
    /// would create a circular reference (potentialManager is already a
    /// subordinate of employeeId somewhere in the chain).
    /// Search is scoped to the current user's tenant to avoid cross-tenant cycles.
    /// </summary>
    private async Task<bool> WouldCreateManagerCycleAsync(Guid employeeId, Guid potentialManagerId)
    {
        var visited = new HashSet<Guid> { employeeId };
        var current = potentialManagerId;

        // Get the tenant IDs to scope the cycle detection
        List<int>? tenantIds = null;
        if (CurrentUser != null && !_tenantContext.IsSuperAdmin(CurrentUser))
        {
            tenantIds = await _tenantContext.GetAuthorizedTenantIdsAsync(CurrentUser);
        }

        while (true)
        {
            if (visited.Contains(current))
                return true;

            visited.Add(current);

            var query = _db.Employees.Where(e => e.Id == current);

            // Scope the search to tenant if applicable
            if (tenantIds != null && tenantIds.Count > 0)
            {
                query = query.Where(e => e.TenantId.HasValue && tenantIds.Contains(e.TenantId.Value));
            }

            var manager = await query
                .Select(e => e.ManagerId)
                .FirstOrDefaultAsync();

            if (manager == null)
                return false;

            current = manager.Value;
        }
    }
}
