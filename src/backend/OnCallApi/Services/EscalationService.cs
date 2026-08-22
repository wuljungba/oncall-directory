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
    private readonly ITeamsNotificationService? _teams;

    public EscalationService(AppDbContext db, ILogger<EscalationService> logger, ITeamsNotificationService? teams = null)
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
    /// Acknowledge/resolve an escalation event. Once acknowledged, no further
    /// escalation tiers will fire for that shift until the next response window
    /// is missed (i.e. the event's shift ends and a new one begins).
    /// </summary>
    public async Task<EscalationEvent> AcknowledgeEventAsync(int eventId)
    {
        var escEvent = await _db.EscalationEvents
            .Include(e => e.Employee)
            .FirstOrDefaultAsync(e => e.Id == eventId)
            ?? throw new KeyNotFoundException($"Escalation event {eventId} not found.");

        escEvent.Status = "resolved";
        escEvent.ResolvedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Escalation event {EventId} acknowledged by {Employee}",
            eventId, escEvent.Employee?.FirstName ?? "unknown");

        return escEvent;
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
                    // An acknowledged shift has a confirmed holder — there is nothing to
                    // escalate. Before this existed the engine escalated EVERY active
                    // shift, tier after tier, purely because time had passed.
                    && s.AcknowledgedAt == null
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
        // Route to the coverage holder for this tier: tier 1 -> the secondary backup,
        // tier 2+ -> the tertiary backup, falling back to the primary only if no backup
        // is assigned. This makes escalation reach the BACKUP instead of re-pinging the
        // same primary who did not answer.
        var target = await ResolveEscalationTargetAsync(shift, tier);

        var escEvent = new EscalationEvent
        {
            PolicyId = policy.Id,
            EmployeeId = target?.Id ?? shift.EmployeeId,
            ShiftId = shift.Id,
            Tier = tier,
            Status = "pending",
            Details = $"Tier {tier} escalation for {target?.FirstName} {target?.LastName} (policy: {policy.Name})",
        };

        _db.EscalationEvents.Add(escEvent);

        // Notify the target via Teams (and any configured channel later).
        //
        // Whether this reached anyone is recorded, not assumed. Delivery previously failed
        // silently in three places — a target with no AzureAdObjectId was skipped without
        // a word, the notification service swallowed every exception, and Graph returned
        // without sending when no 1:1 chat existed. The escalation was still written as
        // "pending", indistinguishable from one that had genuinely gone out.
        var deliveryFailure = await NotifyTargetAsync(policy, shift, target, tier);

        if (deliveryFailure != null)
        {
            escEvent.Status = "notify_failed";
            escEvent.Details += $" — NOT DELIVERED: {deliveryFailure}";

            // Safety-critical: nobody was told. This must be loud enough to alert on.
            _logger.LogError(
                "ESCALATION NOT DELIVERED for shift {ShiftId} tier {Tier} (policy {Policy}): {Reason}",
                shift.Id, tier, policy.Name, deliveryFailure);
        }

        await _db.SaveChangesAsync();
        _logger.LogWarning("Escalation fired: {Details}", escEvent.Details);
    }

    /// <summary>
    /// Attempts to notify the escalation target. Returns null on success, or a reason
    /// describing why nobody was reached.
    /// </summary>
    private async Task<string?> NotifyTargetAsync(
        EscalationPolicy policy, Shift shift, Employee? target, int tier)
    {
        if (target == null)
            return "no escalation target could be resolved";

        if (_teams == null)
            return "no notification channel is configured";

        if (string.IsNullOrEmpty(target.AzureAdObjectId))
            return $"{target.FirstName} {target.LastName} has no Microsoft identity to notify";

        var deptName = shift.Schedule?.Department?.Name ?? "Unknown";
        var delivered = await _teams.SendEscalationAsync(
            target.AzureAdObjectId,
            deptName,
            $"Tier {tier}",
            $"Escalation: the primary did not respond within the policy window. Policy: {policy.Name}");

        return delivered ? null : "Teams delivery failed";
    }

    /// <summary>
    /// Picks who to contact for an escalation: tier 1 -> the secondary shift holder,
    /// tier 2+ -> the tertiary holder, falling back to the shift's own (primary) holder.
    /// </summary>
    private async Task<Employee?> ResolveEscalationTargetAsync(Shift shift, int tier)
    {
        if (shift.Employee == null) return null;

        var targetTier = tier switch
        {
            1 => "secondary",
            _ => "tertiary",
        };

        var backup = await _db.Shifts
            .Include(s => s.Employee)
            .Where(s => s.ScheduleId == shift.ScheduleId
                && s.StartTime == shift.StartTime
                && s.EndTime == shift.EndTime
                && s.Tier == targetTier
                && s.Status != "gap"
                && s.Employee != null)
            .OrderBy(s => s.EmployeeId)
            .FirstOrDefaultAsync();

        // If no dedicated backup is assigned for this tier, fall back to the primary.
        return backup?.Employee ?? shift.Employee;
    }
}
