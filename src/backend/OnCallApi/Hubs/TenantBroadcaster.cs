using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Hubs;

/// <summary>
/// Sends a real-time notification to one tenant's clients.
///
/// Every controller previously called <c>Clients.All.SendAsync</c>, so each customer's
/// staff changes, schedule changes and live code-call incidents were pushed to every
/// connected client of every other customer. Group membership was already gated correctly
/// — it was the broadcast side that ignored it.
///
/// This is injectable rather than a static helper so a test can assert on what was sent,
/// and specifically that <c>Clients.All</c> is never used.
/// </summary>
public interface ITenantBroadcaster
{
    /// <summary>
    /// Sends to the given tenant's group. When the tenant cannot be resolved the message is
    /// NOT sent to everyone — that was a cross-tenant leak, not a delivery guarantee. Set
    /// <paramref name="safetyCritical"/> on the code-call, dispatch and escalation paths so
    /// an undeliverable notification is recorded in the audit trail rather than vanishing.
    /// </summary>
    Task ToTenantAsync(int? tenantId, string method, object payload, bool safetyCritical = false);

    Task<int?> TenantForDepartmentAsync(int? departmentId);
    Task<int?> TenantForEmployeeAsync(Guid employeeId);
    Task<int?> TenantForScheduleAsync(int scheduleId);
    Task<int?> TenantForShiftAsync(int shiftId);
    Task<int?> TenantForPhoneTreeAsync(int phoneTreeId);
    Task<int?> TenantForPhoneTreeNodeAsync(int nodeId);
    Task<int?> TenantForEventAsync(int eventId);
    Task<int?> TenantForEscalationPolicyAsync(int policyId);
}

public class TenantBroadcaster : ITenantBroadcaster
{
    private readonly IHubContext<OnCallNotificationHub> _hub;
    private readonly AppDbContext _db;
    private readonly IAuditService _audit;
    private readonly ILogger<TenantBroadcaster> _logger;

    public TenantBroadcaster(
        IHubContext<OnCallNotificationHub> hub,
        AppDbContext db,
        IAuditService audit,
        ILogger<TenantBroadcaster> logger)
    {
        _hub = hub;
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    public async Task ToTenantAsync(int? tenantId, string method, object payload, bool safetyCritical = false)
    {
        if (tenantId.HasValue)
        {
            await _hub.Clients.Group($"tenant-{tenantId.Value}").SendAsync(method, payload);
            return;
        }

        // Falling back to every client would tell other customers about this one. Dropping
        // the notification is the safe direction; saying so loudly is the required part.
        _logger.LogError(
            "Notification '{Method}' could not be delivered: its tenant could not be resolved. "
            + "It was NOT broadcast to other tenants.", method);

        if (safetyCritical)
        {
            _audit.Enqueue(new AuditLog
            {
                Action = "NotificationUndeliverable",
                ResourceType = "Notification",
                ResourceId = method,
                UserName = "system",
                Details = $"Safety-critical notification '{method}' had no resolvable tenant and was not delivered.",
                Timestamp = DateTime.UtcNow,
            });
        }
    }

    public async Task<int?> TenantForDepartmentAsync(int? departmentId)
    {
        if (departmentId == null) return null;
        return await _db.Departments
            .Where(d => d.Id == departmentId.Value)
            .Select(d => d.TenantId)
            .FirstOrDefaultAsync();
    }

    public async Task<int?> TenantForEmployeeAsync(Guid employeeId)
    {
        return await _db.Employees
            .Where(e => e.Id == employeeId)
            .Select(e => e.TenantId)
            .FirstOrDefaultAsync();
    }

    public async Task<int?> TenantForScheduleAsync(int scheduleId)
    {
        return await _db.Schedules
            .Where(s => s.Id == scheduleId)
            .Select(s => s.Department != null ? s.Department.TenantId : null)
            .FirstOrDefaultAsync();
    }

    public async Task<int?> TenantForShiftAsync(int shiftId)
    {
        return await _db.Shifts
            .Where(s => s.Id == shiftId)
            .Select(s => s.Schedule != null && s.Schedule.Department != null
                ? s.Schedule.Department.TenantId
                : null)
            .FirstOrDefaultAsync();
    }

    public async Task<int?> TenantForPhoneTreeAsync(int phoneTreeId)
    {
        return await _db.PhoneTrees
            .Where(t => t.Id == phoneTreeId)
            .Select(t => t.Department != null ? t.Department.TenantId : null)
            .FirstOrDefaultAsync();
    }

    public async Task<int?> TenantForPhoneTreeNodeAsync(int nodeId)
    {
        return await _db.PhoneTreeNodes
            .Where(n => n.Id == nodeId)
            .Select(n => n.PhoneTree != null && n.PhoneTree.Department != null
                ? n.PhoneTree.Department.TenantId
                : null)
            .FirstOrDefaultAsync();
    }

    public async Task<int?> TenantForEscalationPolicyAsync(int policyId)
    {
        return await _db.EscalationPolicies
            .Where(p => p.Id == policyId)
            .Select(p => p.Department != null ? p.Department.TenantId : null)
            .FirstOrDefaultAsync();
    }

    public async Task<int?> TenantForEventAsync(int eventId)
    {
        return await _db.PhoneTreeEvents
            .Where(e => e.Id == eventId)
            .Select(e => e.PhoneTree != null && e.PhoneTree.Department != null
                ? e.PhoneTree.Department.TenantId
                : null)
            .FirstOrDefaultAsync();
    }
}
