using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Manages escalation policies and auto-escalation logic.
/// Periodically checks if on-call employees have responded and escalates if not.
/// </summary>
public class EscalationService
{
    private readonly AppDbContext _db;
    private readonly ILogger<EscalationService> _logger;
    private readonly TeamsNotificationService? _teams;

    public EscalationService(AppDbContext db, ILogger<EscalationService> logger, TeamsNotificationService? teams = null)
    {
        _db = db;
        _logger = logger;
        _teams = teams;
    }

    public async Task<List<EscalationPolicy>> GetPoliciesAsync(int? departmentId = null)
    {
        var q = _db.EscalationPolicies.Include(p => p.Department).AsQueryable();
        if (departmentId.HasValue) q = q.Where(p => p.DepartmentId == departmentId.Value || p.DepartmentId == null);
        return await q.Where(p => p.IsActive).ToListAsync();
    }

    public async Task<EscalationPolicy> CreatePolicyAsync(EscalationPolicy policy)
    {
        _db.EscalationPolicies.Add(policy);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Escalation policy created: {Name}", policy.Name);
        return policy;
    }

    public async Task<EscalationPolicy> UpdatePolicyAsync(EscalationPolicy policy)
    {
        var existing = await _db.EscalationPolicies.FindAsync(policy.Id)
            ?? throw new KeyNotFoundException($"Policy {policy.Id} not found");
        _db.Entry(existing).CurrentValues.SetValues(policy);
        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task DeletePolicyAsync(int id)
    {
        var policy = await _db.EscalationPolicies.FindAsync(id)
            ?? throw new KeyNotFoundException($"Policy {id} not found");
        _db.EscalationPolicies.Remove(policy);
        await _db.SaveChangesAsync();
    }

    public async Task<List<EscalationEvent>> GetEventsAsync(int? policyId = null, int? limit = 50)
    {
        var q = _db.EscalationEvents
            .Include(e => e.Policy)
            .Include(e => e.Employee)
            .Include(e => e.Shift)
            .AsQueryable();

        if (policyId.HasValue) q = q.Where(e => e.PolicyId == policyId.Value);
        return await q.OrderByDescending(e => e.TriggeredAt).Take(limit ?? 50).ToListAsync();
    }

    /// <summary>
    /// Check all active shifts and fire escalations for anyone past their response window.
    /// Called periodically by EscalationBackgroundService.
    /// </summary>
    public async Task CheckAndEscalateAsync()
    {
        var now = DateTime.UtcNow;
        var policies = await _db.EscalationPolicies.Where(p => p.IsActive).ToListAsync();

        foreach (var policy in policies)
        {
            // Find active shifts in this department that started > MaxResponseMinutes ago
            var departmentId = policy.DepartmentId;
            var activeShifts = await _db.Shifts
                .Include(s => s.Employee)
                .Include(s => s.Schedule)
                .Where(s => s.StartTime <= now && s.EndTime >= now
                    && s.Status != "gap"
                    && (departmentId == null || s.Schedule!.DepartmentId == departmentId)
                    && s.Employee != null)
                .ToListAsync();

            foreach (var shift in activeShifts)
            {
                // Check if already escalated recently
                var recentEvent = await _db.EscalationEvents
                    .Where(e => e.ShiftId == shift.Id && e.Status == "pending")
                    .OrderByDescending(e => e.TriggeredAt)
                    .FirstOrDefaultAsync();

                if (recentEvent != null)
                {
                    // Already escalated — check if it's time for the next tier
                    var minutesSinceTrigger = (now - recentEvent.TriggeredAt).TotalMinutes;
                    if (minutesSinceTrigger >= policy.MaxResponseMinutes && recentEvent.Tier < policy.EscalationTierCount)
                    {
                        await FireEscalation(policy, shift, recentEvent.Tier + 1);
                    }
                    continue;
                }

                // Shift started more than MaxResponseMinutes ago with no acknowledgment
                var minutesSinceStart = (now - shift.StartTime).TotalMinutes;
                if (minutesSinceStart >= policy.MaxResponseMinutes)
                {
                    await FireEscalation(policy, shift, 1);
                }
            }
        }
    }

    private async Task FireEscalation(EscalationPolicy policy, Shift shift, int tier)
    {
        if (shift.Employee == null) return;

        var escEvent = new EscalationEvent
        {
            PolicyId = policy.Id,
            EmployeeId = shift.EmployeeId,
            ShiftId = shift.Id,
            Tier = tier,
            Status = "pending",
            Details = $"Tier {tier} escalation for {shift.Employee.FirstName} {shift.Employee.LastName} (policy: {policy.Name})",
        };

        _db.EscalationEvents.Add(escEvent);

        // Notify via Teams
        if (_teams != null && !string.IsNullOrEmpty(shift.Employee.AzureAdObjectId))
        {
            var deptName = shift.Schedule?.Department?.Name ?? "Unknown";
            await _teams.SendEscalationAsync(
                shift.Employee.AzureAdObjectId,
                deptName,
                $"Tier {tier}",
                $"You have an escalation for your on-call shift. Policy: {policy.Name}");
        }

        await _db.SaveChangesAsync();
        _logger.LogWarning("Escalation fired: {Details}", escEvent.Details);
    }
}
