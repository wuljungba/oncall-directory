using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace OnCallApi.Hubs;

/// <summary>
/// Real-time notifications for on-call schedule changes.
/// Frontend connects via SignalR to receive live updates.
/// </summary>
[Authorize]
public class OnCallNotificationHub : Hub
{
    private readonly ILogger<OnCallNotificationHub> _logger;

    public OnCallNotificationHub(ILogger<OnCallNotificationHub> logger)
    {
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

        // Join user to their tenant group(s) for tenant-scoped notifications
        foreach (var claim in Context.User?.Claims.Where(c => c.Type.StartsWith("TenantId:")) ?? [])
        {
            var tenantId = claim.Type.Replace("TenantId:", "");
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

    /// <summary>Join a department notification group.</summary>
    public async Task JoinDepartment(int departmentId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"dept-{departmentId}");
    }

    /// <summary>Leave a department notification group.</summary>
    public async Task LeaveDepartment(int departmentId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"dept-{departmentId}");
    }

    /// <summary>Join a tenant notification group.</summary>
    public async Task JoinTenant(int tenantId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant-{tenantId}");
    }

    /// <summary>Leave a tenant notification group.</summary>
    public async Task LeaveTenant(int tenantId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"tenant-{tenantId}");
    }
}
