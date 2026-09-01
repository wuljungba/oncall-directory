using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

public class DirectoryService : IDirectoryService
{
    private readonly AppDbContext _db;
    private readonly ITenantScope _scope;

    public DirectoryService(AppDbContext db, ITenantScope scope)
    {
        _db = db;
        _scope = scope;
    }

    // Read paths are AsNoTracking deliberately, and it is not only a performance choice.
    // With tracking on, EF relationship fixup back-populates the reverse navigations on
    // every entity it loads -- Department.Employees gets filled in with each tracked
    // employee, whose own Department is filled in, and so on. Serializing that graph turned
    // a 17-row directory into a 3 GB response. ReferenceHandler.IgnoreCycles only breaks
    // true cycles; it does not stop the breadth. Not tracking means the reverse navigations
    // stay empty and each row serializes as itself.

    /// <summary>
    /// Restricts an employee query to the caller's tenants. A caller with no tenants sees
    /// nothing — the filter is never skipped, which is what made this fail open before.
    /// </summary>
    private async Task<IQueryable<Employee>> ScopeEmployeesAsync(IQueryable<Employee> query)
    {
        var tenantIds = await _scope.AllowedTenantIdsAsync();
        if (tenantIds == null) return query;
        return query.Where(e => e.TenantId.HasValue && tenantIds.Contains(e.TenantId.Value));
    }

    /// <summary>Phone trees belong to a tenant through their department.</summary>
    private async Task<IQueryable<PhoneTree>> ScopePhoneTreesAsync(IQueryable<PhoneTree> query)
    {
        var tenantIds = await _scope.AllowedTenantIdsAsync();
        if (tenantIds == null) return query;
        return query.Where(t => t.Department != null
            && t.Department.TenantId.HasValue
            && tenantIds.Contains(t.Department.TenantId.Value));
    }

    public async Task<List<Employee>> SearchEmployeesAsync(string query, int? departmentId = null)
    {
        var q = _db.Employees
            .AsNoTracking()
            .Include(e => e.Department)
            .Where(e => e.IsActive);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var search = query.ToLower();

            // Unit names are written every which way -- "3North", "3 North", "3-North" --
            // and whoever is searching has no idea which the import happened to use. Both
            // sides drop spaces and punctuation so all three spellings find each other.
            var compact = new string(query.Where(char.IsLetterOrDigit).ToArray()).ToLower();

            q = q.Where(e =>
                e.FirstName.ToLower().Contains(search) ||
                e.LastName.ToLower().Contains(search) ||
                (e.Title != null && e.Title.ToLower().Contains(search)) ||
                (e.Specialty != null && e.Specialty.ToLower().Contains(search)) ||
                (e.ClinicalRole != null && e.ClinicalRole.ToLower().Contains(search)) ||
                (e.OfficeLocation != null && e.OfficeLocation.ToLower().Contains(search)) ||
                (e.Email != null && e.Email.ToLower().Contains(search)) ||
                (e.DisplayName != null && e.DisplayName.ToLower().Contains(search)) ||
                (compact.Length > 0 && e.DisplayName != null
                    && e.DisplayName.ToLower().Replace(" ", "").Replace("-", "").Replace(".", "")
                        .Contains(compact)) ||
                (e.Extension != null && e.Extension.Contains(search)) ||
                (e.Department != null && e.Department.Name.ToLower().Contains(search)));
        }

        if (departmentId.HasValue)
            q = q.Where(e => e.DepartmentId == departmentId.Value);

        q = await ScopeEmployeesAsync(q);
        return await q.OrderBy(e => e.LastName).ThenBy(e => e.FirstName).ToListAsync();
    }

    public async Task<List<Employee>> GetDepartmentEmployeesAsync(int departmentId)
    {
        var q = await ScopeEmployeesAsync(_db.Employees
            .AsNoTracking()
            .Include(e => e.Department)
            .Where(e => e.DepartmentId == departmentId && e.IsActive));

        return await q.OrderBy(e => e.LastName).ToListAsync();
    }

    public async Task<Employee?> GetEmployeeByIdAsync(Guid id)
    {
        var q = await ScopeEmployeesAsync(_db.Employees
            .AsNoTracking()
            .Include(e => e.Department)
            .Include(e => e.Manager)
            .Where(e => e.Id == id));

        return await q.FirstOrDefaultAsync();
    }

    public async Task<Employee?> GetEmployeeByEmailAsync(string email)
    {
        // No address, no match. Email is optional now, and comparing null to null would
        // otherwise hand back an arbitrary department contact for a blank lookup.
        if (string.IsNullOrWhiteSpace(email)) return null;

        var normalized = email.ToLower();
        var q = await ScopeEmployeesAsync(_db.Employees
            .AsNoTracking()
            .Include(e => e.Department)
            .Where(e => e.Email != null && e.Email.ToLower() == normalized));

        return await q.FirstOrDefaultAsync();
    }

    public async Task<List<PhoneTree>> GetPhoneTreesAsync(int? departmentId = null)
    {
        var query = _db.PhoneTrees
            .AsNoTracking()
            .Include(t => t.Nodes.OrderBy(n => n.Order))
            .ThenInclude(n => n.Employee)
            .AsQueryable();

        if (departmentId.HasValue)
            query = query.Where(t => t.DepartmentId == departmentId.Value);

        query = await ScopePhoneTreesAsync(query);
        return await query.Where(t => t.IsActive).ToListAsync();
    }

    public async Task<PhoneTree?> GetPhoneTreeByIdAsync(int id)
    {
        var query = await ScopePhoneTreesAsync(_db.PhoneTrees
            .Include(t => t.Nodes.OrderBy(n => n.Order))
            .ThenInclude(n => n.Employee)
            .Where(t => t.Id == id));

        return await query.FirstOrDefaultAsync();
    }

    public async Task<List<Employee>> GetOnCallEmployeesAsync(int? departmentId = null)
    {
        var now = DateTime.UtcNow;
        var query = _db.Employees
            .AsNoTracking()
            .Include(e => e.Department)
            .Where(e => e.OnCallStatus && e.IsActive);

        if (departmentId.HasValue)
            query = query.Where(e => e.DepartmentId == departmentId.Value);

        query = await ScopeEmployeesAsync(query);
        return await query.ToListAsync();
    }

    /// <summary>
    /// Resolves a phone tree the caller is allowed to touch, or throws.
    ///
    /// Reads were scoped through ScopePhoneTreesAsync but writes were not, so a scheduler
    /// in one tenant could rewrite another tenant's escalation tree — repointing or
    /// emptying it so their next code call paged nobody. Every mutator now resolves through
    /// the same scoped query, and an out-of-tenant id reads as "not found".
    /// </summary>
    private async Task<PhoneTree> RequireTreeAsync(int treeId)
    {
        var scoped = await ScopePhoneTreesAsync(_db.PhoneTrees.Where(t => t.Id == treeId));
        return await scoped.FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException($"Phone tree {treeId} not found");
    }

    /// <summary>
    /// Confirms a department the caller may place a tree in.
    ///
    /// A tree reaches its tenant only through its department, so a null department would
    /// create a tree that no scoped read can ever see again — an orphan nobody can find or
    /// fix. Only an unrestricted caller (super admin, background service) may create one.
    /// </summary>
    private async Task EnsureDepartmentAllowedAsync(int? departmentId)
    {
        var tenantIds = await _scope.AllowedTenantIdsAsync();

        // Existence is checked for everyone, super admins included. Returning early for an
        // unrestricted caller meant a typo'd department id reached SaveChanges and came
        // back as an opaque 500 instead of a 404.
        if (tenantIds == null)
        {
            if (departmentId == null) return;
            var exists = await _db.Departments.AnyAsync(d => d.Id == departmentId.Value);
            if (!exists)
                throw new KeyNotFoundException($"Department {departmentId} not found");
            return;
        }

        if (departmentId == null)
            throw new KeyNotFoundException("A phone tree must belong to a department.");

        var allowed = await _db.Departments
            .AnyAsync(d => d.Id == departmentId.Value
                && d.TenantId.HasValue
                && tenantIds.Contains(d.TenantId.Value));

        if (!allowed)
            throw new KeyNotFoundException($"Department {departmentId} not found");
    }

    public async Task<PhoneTree> CreatePhoneTreeAsync(PhoneTree tree)
    {
        await EnsureDepartmentAllowedAsync(tree.DepartmentId);
        _db.PhoneTrees.Add(tree);
        await _db.SaveChangesAsync();
        return tree;
    }

    public async Task<PhoneTree> UpdatePhoneTreeAsync(PhoneTree tree)
    {
        var existing = await RequireTreeAsync(tree.Id);
        // Guard the destination too, so an update cannot move a tree into another tenant.
        await EnsureDepartmentAllowedAsync(tree.DepartmentId);
        _db.Entry(existing).CurrentValues.SetValues(tree);
        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task DeletePhoneTreeAsync(int id)
    {
        var tree = await RequireTreeAsync(id);
        _db.PhoneTrees.Remove(tree);
        await _db.SaveChangesAsync();
    }

    public async Task<PhoneTreeNode> AddNodeAsync(int treeId, PhoneTreeNode node)
    {
        await RequireTreeAsync(treeId);
        node.PhoneTreeId = treeId;
        _db.PhoneTreeNodes.Add(node);
        await _db.SaveChangesAsync();
        return node;
    }

    public async Task UpdateNodeAsync(PhoneTreeNode node)
    {
        var existing = await _db.PhoneTreeNodes.FindAsync(node.Id)
            ?? throw new KeyNotFoundException($"Phone tree node {node.Id} not found");

        // Both the tree the node is in and the tree it would move to must be in scope.
        await RequireTreeAsync(existing.PhoneTreeId);
        if (node.PhoneTreeId != existing.PhoneTreeId)
            await RequireTreeAsync(node.PhoneTreeId);

        _db.Entry(existing).CurrentValues.SetValues(node);
        await _db.SaveChangesAsync();
    }

    public async Task RemoveNodeAsync(int nodeId)
    {
        var node = await _db.PhoneTreeNodes.FindAsync(nodeId)
            ?? throw new KeyNotFoundException($"Phone tree node {nodeId} not found");
        await RequireTreeAsync(node.PhoneTreeId);
        _db.PhoneTreeNodes.Remove(node);
        await _db.SaveChangesAsync();
    }

    public async Task ReorderNodesAsync(int treeId, List<int> nodeIds)
    {
        await RequireTreeAsync(treeId);

        var nodes = await _db.PhoneTreeNodes
            .Where(n => n.PhoneTreeId == treeId)
            .ToListAsync();

        for (int i = 0; i < nodeIds.Count; i++)
        {
            var node = nodes.FirstOrDefault(n => n.Id == nodeIds[i]);
            if (node != null) node.Order = i + 1;
        }

        await _db.SaveChangesAsync();
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
