using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using OnCallApi.Services;

namespace OnCallApi.Hubs;

/// <summary>
/// Real-time notifications for on-call schedule changes.
/// Frontend connects via SignalR to receive live updates.
///
/// Group membership is authorization, not routing: the tenant groups carry live code-call
/// traffic including patient location and incident notes. Joining is therefore always
/// checked against the caller's own tenants — a client asking to join a group is a request,
/// never an instruction.
/// </summary>
[Authorize]
public class OnCallNotificationHub : Hub
{
    private readonly ITenantContextService _tenants;
    private readonly ILogger<OnCallNotificationHub> _logger;

    public OnCallNotificationHub(ITenantContextService tenants, ILogger<OnCallNotificationHub> logger)
    {
        _tenants = tenants;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier ?? "unknown";
        _logger.LogInformation("Client connected: {UserId}", userId);

        // Join user to their department group for targeted notifications
        var departmentClaim = Context.User?.FindFirst("department")?.Value;
        if (departmentClaim != null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"dept-{departmentClaim}");
        }

        // Tenant groups come from the database, not from the client. Claims are used only
        // to identify the principal; TenantContextService decides what they may see.
        foreach (var tenantId in await AuthorizedTenantsAsync())
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant-{tenantId}");
            _logger.LogInformation("Client {UserId} joined tenant group tenant-{TenantId}", userId, tenantId);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {UserId}", Context.UserIdentifier);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Join a department notification group, if the caller may see that department.
    /// </summary>
    public async Task JoinDepartment(int departmentId)
    {
        if (!await CanAccessDepartmentAsync(departmentId))
        {
            _logger.LogWarning(
                "Client {UserId} denied department group dept-{DepartmentId}",
                Context.UserIdentifier ?? "unknown", departmentId);
            throw new HubException("Not authorized for that department.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"dept-{departmentId}");
    }

    /// <summary>Leave a department notification group. Always permitted.</summary>
    public async Task LeaveDepartment(int departmentId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"dept-{departmentId}");
    }

    /// <summary>
    /// Join a tenant notification group, if the caller administers or is granted that
    /// tenant. Previously unchecked: any authenticated user could invoke this with an
    /// arbitrary id and receive another tenant's live code-call feed.
    /// </summary>
    public async Task JoinTenant(int tenantId)
    {
        var authorized = await AuthorizedTenantsAsync();
        if (!authorized.Contains(tenantId))
        {
            _logger.LogWarning(
                "Client {UserId} denied tenant group tenant-{TenantId}",
                Context.UserIdentifier ?? "unknown", tenantId);
            throw new HubException("Not authorized for that tenant.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant-{tenantId}");
    }

    /// <summary>Leave a tenant notification group. Always permitted.</summary>
    public async Task LeaveTenant(int tenantId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"tenant-{tenantId}");
    }

    private async Task<List<int>> AuthorizedTenantsAsync() =>
        Context.User is null ? [] : await _tenants.GetAuthorizedTenantIdsAsync(Context.User);

    /// <summary>
    /// A department is reachable when it belongs to one of the caller's tenants. Super
    /// admins may join any; the department's own tenant is resolved from the database so a
    /// client cannot assert it.
    /// </summary>
    private async Task<bool> CanAccessDepartmentAsync(int departmentId)
    {
        if (Context.User is null) return false;
        if (_tenants.IsSuperAdmin(Context.User)) return true;

        var authorized = await AuthorizedTenantsAsync();
        if (authorized.Count == 0) return false;

        var departmentTenantId = await _tenants.GetDepartmentTenantIdAsync(departmentId);
        return departmentTenantId.HasValue && authorized.Contains(departmentTenantId.Value);
    }
}
