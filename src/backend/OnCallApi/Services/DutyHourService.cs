using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

public class DutyHourService : IDutyHourService
{
    private readonly AppDbContext _db;
    private readonly ILogger<DutyHourService> _logger;

    public DutyHourService(AppDbContext db, ILogger<DutyHourService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<DutyHourRule>> GetRulesAsync(int? departmentId = null)
    {
        var query = _db.DutyHourRules.AsQueryable();
        if (departmentId.HasValue)
            query = query.Where(r => r.DepartmentId == null || r.DepartmentId == departmentId.Value);
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
        await _db.SaveChangesAsync();

        return await _db.DutyHourViolations
            .Include(v => v.Employee).ThenInclude(e => e!.Department)
            .Include(v => v.Rule)
            .Where(v => v.EmployeeId == employeeId)
            .OrderByDescending(v => v.ViolatedAt)
            .ToListAsync();
    }

    public async Task<List<DutyHourViolation>> CheckAllComplianceAsync(DateTime? from = null, DateTime? to = null, int? departmentId = null)
    {
        var query = _db.Employees.Where(e => e.IsActive);
        if (departmentId.HasValue)
            query = query.Where(e => e.DepartmentId == departmentId.Value);

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