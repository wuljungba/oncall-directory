using ValidationException = System.ComponentModel.DataAnnotations.ValidationException;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Validators;

namespace OnCallApi.Services;

public class AdminService : IAdminService
{
    private readonly AppDbContext _db;
    private readonly ILogger<AdminService> _logger;
    private readonly ITenantContextService _tenantContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuditService _audit;

    public AdminService(
        AppDbContext db,
        ILogger<AdminService> logger,
        ITenantContextService tenantContext,
        IHttpContextAccessor httpContextAccessor,
        IAuditService audit)
    {
        _db = db;
        _logger = logger;
        _tenantContext = tenantContext;
        _httpContextAccessor = httpContextAccessor;
        _audit = audit;
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
                    throw new ValidationException($"Subscription (tenant) {requested.Value} does not exist.");
            }
            return requested;
        }
        return await GetCurrentUserTenantId();
    }

    /// <summary>
    /// Confirms the foreign keys on an employee request actually resolve.
    ///
    /// Nothing checked these, so a departmentId or managerId that did not exist reached
    /// SaveChanges and surfaced as a raw DbUpdateException -- an HTTP 500 with no clue
    /// which field was wrong. The bulk importer has validated department ids all along
    /// (BulkImportService.ValidateDepartmentIdsAsync); the single-record admin path had
    /// simply been missed.
    ///
    /// ValidationException maps to 400 in ExceptionHandlingMiddleware. InvalidOperationException
    /// is left to mean "conflicts with what already exists" (a duplicate email), which the
    /// controller maps to 409.
    /// </summary>
    private async Task ValidateEmployeeReferencesAsync(int? departmentId, Guid? managerId)
    {
        if (departmentId.HasValue)
        {
            var departmentExists = await _db.Departments.AnyAsync(d => d.Id == departmentId.Value);
            if (!departmentExists)
                throw new ValidationException($"Department {departmentId.Value} does not exist.");
        }

        if (managerId.HasValue)
        {
            var managerExists = await _db.Employees.AnyAsync(e => e.Id == managerId.Value);
            if (!managerExists)
                throw new ValidationException($"Manager {managerId.Value} does not exist.");
        }
    }

    /// <summary>
    /// Normalizes a phone number the same way the importer does, or reports why it cannot.
    ///
    /// Employee.OfficePhone/MobilePhone/PagerNumber carry a [RegularExpression] demanding
    /// E.164, but nothing enforced it on this path, so whatever the user typed was stored
    /// raw -- including values the dispatch path could never dial. Normalizing rather than
    /// rejecting means "(202) 555-0134" is accepted and stored as "+12025550134", which is
    /// what someone typing into the admin form actually expects.
    /// </summary>
    private static string? NormalizePhoneOrThrow(string? raw, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var normalized = PhoneValidation.NormalizeToDialable(raw);
        if (normalized == null)
        {
            throw new ValidationException(
                $"{fieldName} could not be read as a dialable phone number. "
                + "Use a full number such as (202) 555-0134 or +12025550134, without an extension.");
        }

        return normalized;
    }

    /// <summary>
    /// Anything unrecognised becomes "Person", the stricter of the two. A typo in this
    /// field must not be a way to create a row that skips the name and email checks.
    /// </summary>
    private static string ResolveContactType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ContactType.Person;

        return string.Equals(raw.Trim(), ContactType.Department, StringComparison.OrdinalIgnoreCase)
            ? ContactType.Department
            : ContactType.Person;
    }

    /// <summary>Keeps the digits of an extension, or reports what is wrong with it.</summary>
    private static string? NormalizeExtensionOrThrow(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var (_, extension) = PhoneValidation.SplitExtension(raw);

        // A value that survives with no extension found is something like "555-0134" -- a
        // full number typed into the extension box, which would be dialled internally and
        // reach nobody. Say so rather than storing it.
        if (extension == null)
        {
            throw new ValidationException(
                $"Extension '{raw.Trim()}' is not an extension. Enter digits only, e.g. 3434.");
        }

        return extension;
    }

    /// <summary>
    /// Enforces what the request DTOs no longer can, now that name and email are optional
    /// there: a person still needs a name and an address, and a department contact needs a
    /// label and some way to be reached.
    ///
    /// Without the second half a "Department" row is a name in the directory that dials
    /// nowhere -- worse than absent, because it looks like a route someone can use.
    /// </summary>
    private static void RequireContactShape(
        string contactType, string? firstName, string? lastName, string? email,
        string? displayName, string? officePhone, string? mobilePhone, string? pagerNumber,
        string? extension)
    {
        if (contactType == ContactType.Department)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                throw new ValidationException("A department contact needs a name to show, e.g. '3North'.");

            var reachable = !string.IsNullOrWhiteSpace(officePhone)
                || !string.IsNullOrWhiteSpace(mobilePhone)
                || !string.IsNullOrWhiteSpace(pagerNumber)
                || !string.IsNullOrWhiteSpace(extension);

            if (!reachable)
                throw new ValidationException("A department contact needs a phone number or an extension.");

            return;
        }

        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
            throw new ValidationException("First name and last name are required.");

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ValidationException(
                "Email is required for a person. To add a unit or service line instead, "
                + "set contactType to 'Department'.");
        }
    }

    // ── Employees ──

    public async Task<List<Employee>> GetAllEmployeesAsync(bool includeInactive = false)
    {
        // AsNoTracking is load-bearing here, not just a performance choice. With tracking
        // on, EF relationship fixup back-populates Department.Employees with every loaded
        // employee, so each employee serializes a department containing all the others --
        // output grows with the square of the employees in a department. A 5,000-row
        // directory produced an 18 GB response. ReferenceHandler.IgnoreCycles only breaks
        // ancestor cycles; it does not stop that breadth.
        var query = _db.Employees
            .AsNoTracking()
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
            .AsNoTracking()
            .Include(e => e.Department)
            .Include(e => e.Manager)
            .Include(e => e.DirectReports);

        query = await FilterEmployeesByTenant(query);

        return await query.FirstOrDefaultAsync(e => e.Id == id);
    }

    public async Task<Employee> CreateEmployeeAsync(CreateEmployeeRequest request)
    {
        var tenantId = await ResolveCreateTenantId(request.TenantId);
        await ValidateEmployeeReferencesAsync(request.DepartmentId, request.ManagerId);

        var contactType = ResolveContactType(request.ContactType);
        var extension = NormalizeExtensionOrThrow(request.Extension);
        RequireContactShape(contactType, request.FirstName, request.LastName,
            request.Email, request.DisplayName, request.OfficePhone, request.MobilePhone,
            request.PagerNumber, extension);

        // One employee per email (onboarding standard). Prevents duplicate directory
        // entries for the same person no matter which path creates them.
        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            var candidate = request.Email.ToLowerInvariant().Trim();
            var existingEmail = await _db.Employees
                .AnyAsync(e => e.Email != null && e.Email.ToLower() == candidate);
            if (existingEmail)
                throw new InvalidOperationException("An employee with this email already exists.");
        }

        var employee = new Employee
        {
            Id = Guid.NewGuid(),
            AzureAdObjectId = request.AzureAdObjectId ?? string.Empty,
            FirstName = request.FirstName ?? string.Empty,
            LastName = request.LastName ?? string.Empty,
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            ContactType = contactType,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
            Extension = extension,
            Title = request.Title,
            Specialty = request.Specialty,
            ClinicalRole = request.ClinicalRole,
            OfficePhone = NormalizePhoneOrThrow(request.OfficePhone, "Office phone"),
            MobilePhone = NormalizePhoneOrThrow(request.MobilePhone, "Mobile phone"),
            PagerNumber = NormalizePhoneOrThrow(request.PagerNumber, "Pager number"),
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

        // Same rule as the importer: an extension with no number becomes a dialable
        // number only when the subscription has a dial plan to build one from.
        DialPlan.ApplyExtensionPrefix(
            employee, await DialPlan.ResolveExtensionPrefixAsync(_db, tenantId));

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
        var created = (await _db.Employees
            .Include(e => e.Department)
            .Include(e => e.Manager)
            .FirstAsync(e => e.Id == employee.Id))!;

        var userId = Guid.Empty;
        var uid = CurrentUser?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? CurrentUser?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(uid, out var parsedUid)) userId = parsedUid;

        _audit.Enqueue(new OnCallApi.Models.AuditLog
        {
            UserId = userId,
            UserName = CurrentUser?.Identity?.Name ?? "",
            Action = "Created",
            ResourceType = "Employee",
            ResourceId = created.Id.ToString(),
            Details = $"Source={created.Source};Email={created.Email}",
            TenantId = created.TenantId,
            Timestamp = DateTime.UtcNow,
        });

        return created;
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

        await ValidateEmployeeReferencesAsync(request.DepartmentId, request.ManagerId);

        // Prevent circular manager reference
        if (request.ManagerId.HasValue && request.ManagerId.Value == id)
            throw new InvalidOperationException("An employee cannot be their own manager.");

        if (request.ManagerId.HasValue)
        {
            var wouldCycle = await WouldCreateManagerCycleAsync(id, request.ManagerId.Value);
            if (wouldCycle)
                throw new InvalidOperationException("This assignment would create a circular manager reference.");
        }

        var contactType = ResolveContactType(request.ContactType ?? existing.ContactType);
        var extension = NormalizeExtensionOrThrow(request.Extension);
        RequireContactShape(contactType, request.FirstName, request.LastName,
            request.Email, request.DisplayName, request.OfficePhone, request.MobilePhone,
            request.PagerNumber, extension);

        existing.FirstName = request.FirstName ?? string.Empty;
        existing.LastName = request.LastName ?? string.Empty;
        existing.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        existing.ContactType = contactType;
        existing.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        existing.Extension = extension;
        existing.Title = request.Title;
        existing.Specialty = request.Specialty;
        existing.ClinicalRole = request.ClinicalRole;
        existing.OfficePhone = NormalizePhoneOrThrow(request.OfficePhone, "Office phone");
        existing.MobilePhone = NormalizePhoneOrThrow(request.MobilePhone, "Mobile phone");
        existing.PagerNumber = NormalizePhoneOrThrow(request.PagerNumber, "Pager number");
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

    public async Task DeleteEmployeeAsync(Guid id)
    {
        var employee = await _db.Employees.FindAsync(id)
            ?? throw new KeyNotFoundException($"Employee {id} not found.");

        // Ensure sub-admin can only delete employees in their tenant
        if (!_tenantContext.IsSuperAdmin(CurrentUser!))
        {
            var tenantIds = await _tenantContext.GetAuthorizedTenantIdsAsync(CurrentUser!);
            if (!employee.TenantId.HasValue || !tenantIds.Contains(employee.TenantId.Value))
                throw new KeyNotFoundException($"Employee {id} not found.");
        }

        // Detach any direct reports so the manager link doesn't block the delete.
        var reports = await _db.Employees.Where(r => r.ManagerId == id).ToListAsync();
        foreach (var report in reports)
        {
            report.ManagerId = null;
            report.UpdatedAt = DateTime.UtcNow;
        }

        _db.Employees.Remove(employee);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // References from schedule/time-off/phone-tree rows remain — refuse to corrupt
            // history rather than silently orphaning it.
            throw new InvalidOperationException(
                "This employee is referenced by schedule, time-off, or phone-tree history " +
                "and cannot be permanently deleted. Deactivate the account instead.");
        }
    }

    public async Task<List<Employee>> GetDirectReportsAsync(Guid managerId)
    {
        var query = _db.Employees
            .AsNoTracking()
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
            .AsNoTracking()
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
