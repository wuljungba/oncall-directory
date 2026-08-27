using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

public class DutyHourService : IDutyHourService
{
    private readonly AppDbContext _db;
    private readonly ITenantScope _scope;
    private readonly ILogger<DutyHourService> _logger;

    public DutyHourService(AppDbContext db, ITenantScope scope, ILogger<DutyHourService> logger)
    {
        _db = db;
        _scope = scope;
        _logger = logger;
    }

    /// <summary>
    /// True when the employee is one the caller may evaluate.
    ///
    /// This gates reads AND writes: CheckComplianceAsync persists DutyHourViolation rows,
    /// so an unscoped call did not merely leak another tenant's staff hours, it wrote to
    /// their compliance record.
    /// </summary>
    private async Task<bool> EmployeeInScopeAsync(Guid employeeId)
    {
        var tenantIds = await _scope.AllowedTenantIdsAsync();
        if (tenantIds == null) return true;

        return await _db.Employees.AnyAsync(e => e.Id == employeeId
            && e.TenantId.HasValue
            && tenantIds.Contains(e.TenantId.Value));
    }

    public async Task<List<DutyHourRule>> GetRulesAsync(int? departmentId = null)
    {
        var query = _db.DutyHourRules.AsQueryable();
        if (departmentId.HasValue)
            query = query.Where(r => r.DepartmentId == null || r.DepartmentId == departmentId.Value);

        // A rule reaches a tenant only through its department. Omitting departmentId used
        // to return every tenant's rules; now an unscoped caller sees only their own.
        //
        // Known gap: a rule with no department is "organization-wide", a notion that
        // predates multi-tenancy and has no owner, so those stay visible to everyone.
        // Giving DutyHourRule its own TenantId is the real fix and needs a migration.
        var tenantIds = await _scope.AllowedTenantIdsAsync();
        if (tenantIds != null)
        {
            query = query.Where(r => r.DepartmentId == null
                || (r.Department != null
                    && r.Department.TenantId.HasValue
                    && tenantIds.Contains(r.Department.TenantId.Value)));
        }

        return await query.Where(r => r.IsEnabled).OrderBy(r => r.Name).ToListAsync();
    }

    public async Task<DutyHourRule> CreateRuleAsync(DutyHourRule rule)
    {
        _db.DutyHourRules.Add(rule);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Created duty-hour rule: {Name}", rule.Name);
        return rule;
    }

    public async Task<DutyHourRule> UpdateRuleAsync(DutyHourRule rule)
    {
        var existing = await _db.DutyHourRules.FindAsync(rule.Id)
            ?? throw new KeyNotFoundException($"Duty-hour rule {rule.Id} not found");
        _db.Entry(existing).CurrentValues.SetValues(rule);
        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteRuleAsync(int id)
    {
        var rule = await _db.DutyHourRules.FindAsync(id)
            ?? throw new KeyNotFoundException($"Duty-hour rule {id} not found");
        _db.DutyHourRules.Remove(rule);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Evaluates every enabled rule against an employee's working shifts, persists the
    /// un-resolved violation set, and returns it populated with Employee + Rule navs.
    /// Shifts that are "gap", "swapped" or "covered" count as rest, not work.
    /// </summary>
    public async Task<List<DutyHourViolation>> CheckComplianceAsync(Guid employeeId, DateTime? from = null, DateTime? to = null, int? departmentId = null)
    {
        // Checked before anything is evaluated or persisted: this method writes violation
        // rows, so an out-of-scope employee must not reach it at all.
        if (!await EmployeeInScopeAsync(employeeId)) return new List<DutyHourViolation>();

        var rules = await GetRulesAsync(departmentId);
        if (rules.Count == 0) return new List<DutyHourViolation>();

        var fromDate = from ?? DateTime.UtcNow.AddDays(-30);
        var toDate = to ?? DateTime.UtcNow;

        var shifts = await _db.Shifts
            .Where(s => s.EmployeeId == employeeId
                && s.StartTime >= fromDate
                && s.StartTime <= toDate
                && s.Status != "gap")
            .OrderBy(s => s.StartTime)
            .ToListAsync();

        var working = shifts.Where(s => s.Status != "swapped" && s.Status != "covered").ToList();

        var violations = new List<DutyHourViolation>();

        foreach (var rule in rules)
        {
            // Max shift length
            if (rule.MaxShiftLengthHours > 0)
            {
                foreach (var s in working)
                {
                    var len = (s.EndTime - s.StartTime).TotalHours;
                    if (len > rule.MaxShiftLengthHours)
                        violations.Add(NewViolation(employeeId, rule, s.StartTime,
                            $"Worked a {len:F0}h shift (limit: {rule.MaxShiftLengthHours}h)"));
                }
            }

            // Min rest between consecutive working shifts
            if (rule.MinHoursBetweenShifts > 0)
            {
                for (var i = 1; i < working.Count; i++)
                {
                    var rest = (working[i].StartTime - working[i - 1].EndTime).TotalHours;
                    if (rest < rule.MinHoursBetweenShifts)
                        violations.Add(NewViolation(employeeId, rule, working[i].StartTime,
                            $"Only {rest:F1}h rest before the next shift (require {rule.MinHoursBetweenShifts}h)"));
                }
            }

            // Max hours in a rolling period
            if (rule.MaxHoursPerPeriod > 0)
            {
                var windowStart = toDate.AddDays(-rule.PeriodDays);
                var inWindow = working.Where(s => s.EndTime >= windowStart && s.StartTime <= toDate).ToList();
                var hours = inWindow.Sum(s => (s.EndTime - s.StartTime).TotalHours);
                if (hours > rule.MaxHoursPerPeriod)
                    violations.Add(NewViolation(employeeId, rule, toDate,
                        $"Worked {hours:F0}h in {rule.PeriodDays} days (limit: {rule.MaxHoursPerPeriod}h)"));
            }

            // Max consecutive days
            if (rule.MaxConsecutiveDays > 0)
            {
                var (days, anchor) = CountConsecutiveDays(working);
                if (days > rule.MaxConsecutiveDays)
                    violations.Add(NewViolation(employeeId, rule, anchor,
                        $"Worked {days} consecutive days (limit: {rule.MaxConsecutiveDays})"));
            }
        }

        // Persist: replace the current un-resolved set so the report always matches reality.
        var existing = await _db.DutyHourViolations
            .Where(v => v.EmployeeId == employeeId && !v.IsResolved)
            .ToListAsync();
        _db.DutyHourViolations.RemoveRange(existing);
        _db.DutyHourViolations.AddRange(violations);
        await SaveReplacingViolationsAsync(employeeId);

        return await _db.DutyHourViolations
            .Include(v => v.Employee).ThenInclude(e => e!.Department)
            .Include(v => v.Rule)
            .Where(v => v.EmployeeId == employeeId)
            .OrderByDescending(v => v.ViolatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Commits the "delete the un-resolved set, insert the freshly computed one" write.
    ///
    /// Compliance checking is triggered by a plain GET (/api/compliance/check), so two
    /// overlapping reads race each other: both load the same un-resolved rows, the first
    /// commits its deletes, and the second then issues DELETEs for rows that are already
    /// gone. EF reports that as DbUpdateConcurrencyException ("expected to affect 1 row(s),
    /// but actually affected 0") and the request 500s — opening the Compliance page in two
    /// tabs, or reloading while the first request was still in flight, was enough.
    ///
    /// A row the other writer already deleted is not a conflict worth failing on: the
    /// desired end state (that row gone) has been reached. Detach those entries and commit
    /// the inserts. ExecuteDeleteAsync would be the tidier fix but it is relational-only,
    /// and the test suite runs on the InMemory provider.
    /// </summary>
    private async Task SaveReplacingViolationsAsync(Guid employeeId)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxAttempts)
            {
                var vanished = ex.Entries.Where(e => e.State == EntityState.Deleted).ToList();

                // Only deleted-row conflicts are benign. Anything else is a real conflict
                // and must not be silently retried into a different outcome.
                if (vanished.Count == 0 || vanished.Count != ex.Entries.Count) throw;

                foreach (var entry in vanished) entry.State = EntityState.Detached;

                _logger.LogDebug(
                    "Concurrent compliance check already removed {Count} un-resolved violation(s) "
                    + "for employee {EmployeeId}; continuing (attempt {Attempt})",
                    vanished.Count, employeeId, attempt);
            }
        }
    }

    public async Task<List<DutyHourViolation>> CheckAllComplianceAsync(DateTime? from = null, DateTime? to = null, int? departmentId = null)
    {
        var query = _db.Employees.Where(e => e.IsActive);
        if (departmentId.HasValue)
            query = query.Where(e => e.DepartmentId == departmentId.Value);

        // Without this, omitting departmentId swept every tenant's staff into the check.
        var tenantIds = await _scope.AllowedTenantIdsAsync();
        if (tenantIds != null)
            query = query.Where(e => e.TenantId.HasValue && tenantIds.Contains(e.TenantId.Value));

        var employees = await query.ToListAsync();
        var violations = new List<DutyHourViolation>();
        foreach (var emp in employees)
        {
            violations.AddRange(await CheckComplianceAsync(emp.Id, from, to, departmentId));
        }
        return violations;
    }

    public async Task<int> GetHoursWorkedAsync(Guid employeeId, DateTime from, DateTime to)
    {
        if (!await EmployeeInScopeAsync(employeeId)) return 0;

        var shifts = await _db.Shifts
            .Where(s => s.EmployeeId == employeeId
                && s.StartTime >= from
                && s.EndTime <= to
                && s.Status != "gap"
                && s.Status != "swapped"
                && s.Status != "covered")
            .ToListAsync();

        return (int)shifts.Sum(s => (s.EndTime - s.StartTime).TotalHours);
    }

    public async Task<int> GetConsecutiveDaysAsync(Guid employeeId, DateTime asOf)
    {
        if (!await EmployeeInScopeAsync(employeeId)) return 0;

        var shifts = await _db.Shifts
            .Where(s => s.EmployeeId == employeeId
                && s.EndTime <= asOf
                && s.Status != "gap"
                && s.Status != "swapped"
                && s.Status != "covered")
            .OrderByDescending(s => s.StartTime)
            .ToListAsync();

        return CountConsecutiveDays(shifts).Days;
    }

    private static DutyHourViolation NewViolation(Guid employeeId, DutyHourRule rule, DateTime violatedAt, string description) =>
        new()
        {
            EmployeeId = employeeId,
            RuleId = rule.Id,
            Severity = rule.Severity,
            Description = description,
            ViolatedAt = violatedAt,
            CreatedAt = DateTime.UtcNow,
        };

    private static (int Days, DateTime Anchor) CountConsecutiveDays(List<Shift> shiftsAsc)
    {
        if (shiftsAsc.Count == 0) return (0, DateTime.UtcNow);

        var ordered = shiftsAsc.OrderByDescending(s => s.StartTime).ToList();
        var days = 1;
        var last = ordered[0].StartTime.Date;
        var anchor = ordered[0].StartTime;
        for (var i = 1; i < ordered.Count; i++)
        {
            var diff = (last - ordered[i].StartTime.Date).Days;
            if (diff <= 1)
            {
                days++;
                last = ordered[i].StartTime.Date;
                anchor = ordered[i].StartTime;
            }
            else break;
        }
        return (days, anchor);
    }
}