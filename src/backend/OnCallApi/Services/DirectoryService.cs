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
            .Include(e => e.Department)
            .Where(e => e.IsActive);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var search = query.ToLower();
            q = q.Where(e =>
                e.FirstName.ToLower().Contains(search) ||
                e.LastName.ToLower().Contains(search) ||
                (e.Title != null && e.Title.ToLower().Contains(search)) ||
                (e.Specialty != null && e.Specialty.ToLower().Contains(search)) ||
                (e.ClinicalRole != null && e.ClinicalRole.ToLower().Contains(search)) ||
                (e.OfficeLocation != null && e.OfficeLocation.ToLower().Contains(search)) ||
                e.Email.ToLower().Contains(search) ||
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
            .Include(e => e.Department)
            .Where(e => e.DepartmentId == departmentId && e.IsActive));

        return await q.OrderBy(e => e.LastName).ToListAsync();
    }

    public async Task<Employee?> GetEmployeeByIdAsync(Guid id)
    {
        var q = await ScopeEmployeesAsync(_db.Employees
            .Include(e => e.Department)
            .Include(e => e.Manager)
            .Where(e => e.Id == id));

        return await q.FirstOrDefaultAsync();
    }

    public async Task<Employee?> GetEmployeeByEmailAsync(string email)
    {
        var q = await ScopeEmployeesAsync(_db.Employees
            .Include(e => e.Department)
            .Where(e => e.Email.ToLower() == email.ToLower()));

        return await q.FirstOrDefaultAsync();
    }

    public async Task<List<PhoneTree>> GetPhoneTreesAsync(int? departmentId = null)
    {
        var query = _db.PhoneTrees
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
            .Include(e => e.Department)
            .Where(e => e.OnCallStatus && e.IsActive);

        if (departmentId.HasValue)
            query = query.Where(e => e.DepartmentId == departmentId.Value);

        query = await ScopeEmployeesAsync(query);
        return await query.ToListAsync();
    }

    public async Task<PhoneTree> CreatePhoneTreeAsync(PhoneTree tree)
    {
        _db.PhoneTrees.Add(tree);
        await _db.SaveChangesAsync();
        return tree;
    }

    public async Task<PhoneTree> UpdatePhoneTreeAsync(PhoneTree tree)
    {
        var existing = await _db.PhoneTrees.FindAsync(tree.Id)
            ?? throw new KeyNotFoundException($"Phone tree {tree.Id} not found");
        _db.Entry(existing).CurrentValues.SetValues(tree);
        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task DeletePhoneTreeAsync(int id)
    {
        var tree = await _db.PhoneTrees.FindAsync(id)
            ?? throw new KeyNotFoundException($"Phone tree {id} not found");
        _db.PhoneTrees.Remove(tree);
        await _db.SaveChangesAsync();
    }

    public async Task<PhoneTreeNode> AddNodeAsync(int treeId, PhoneTreeNode node)
    {
        node.PhoneTreeId = treeId;
        _db.PhoneTreeNodes.Add(node);
        await _db.SaveChangesAsync();
        return node;
    }

    public async Task UpdateNodeAsync(PhoneTreeNode node)
    {
        var existing = await _db.PhoneTreeNodes.FindAsync(node.Id)
            ?? throw new KeyNotFoundException($"Phone tree node {node.Id} not found");
        _db.Entry(existing).CurrentValues.SetValues(node);
        await _db.SaveChangesAsync();
    }

    public async Task RemoveNodeAsync(int nodeId)
    {
        var node = await _db.PhoneTreeNodes.FindAsync(nodeId)
            ?? throw new KeyNotFoundException($"Phone tree node {nodeId} not found");
        _db.PhoneTreeNodes.Remove(node);
        await _db.SaveChangesAsync();
    }

    public async Task ReorderNodesAsync(int treeId, List<int> nodeIds)
    {
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
