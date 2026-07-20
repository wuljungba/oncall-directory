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

    public async Task<List<DutyHourViolation>> CheckComplianceAsync(Guid employeeId, DateTime? from = null, DateTime? to = null)
    {
        var violations = new List<DutyHourViolation>();
        var rules = await GetRulesAsync();
        var fromDate = from ?? DateTime.UtcNow.AddDays(-30);
        var toDate = to ?? DateTime.UtcNow.AddDays(30);

        foreach (var rule in rules)
        {
            // Check hours worked in period
            var hours = await GetHoursWorkedAsync(employeeId, fromDate, toDate);
            if (hours > rule.MaxHoursPerPeriod)
            {
                violations.Add(new DutyHourViolation
                {
                    EmployeeId = employeeId,
                    RuleId = rule.Id,
                    Description = $"Worked {hours}h in {rule.PeriodDays} days (limit: {rule.MaxHoursPerPeriod}h)",
                    Severity = 2,
                    ViolatedAt = DateTime.UtcNow,
                });
            }

            // Check consecutive days
            var consecutiveDays = await GetConsecutiveDaysAsync(employeeId, DateTime.UtcNow);
            if (consecutiveDays > rule.MaxConsecutiveDays)
            {
                violations.Add(new DutyHourViolation
                {
                    EmployeeId = employeeId,
                    RuleId = rule.Id,
                    Description = $"Worked {consecutiveDays} consecutive days (limit: {rule.MaxConsecutiveDays})",
                    Severity = 2,
                    ViolatedAt = DateTime.UtcNow,
                });
            }
        }

        return violations;
    }

    public async Task<List<DutyHourViolation>> CheckAllComplianceAsync(DateTime? from = null, DateTime? to = null)
    {
        var violations = new List<DutyHourViolation>();
        var employees = await _db.Employees.Where(e => e.IsActive).ToListAsync();

        foreach (var emp in employees)
        {
            var empViolations = await CheckComplianceAsync(emp.Id, from, to);
            violations.AddRange(empViolations);
        }

        return violations;
    }

    public async Task<int> GetHoursWorkedAsync(Guid employeeId, DateTime from, DateTime to)
    {
        var shifts = await _db.Shifts
            .Where(s => s.EmployeeId == employeeId && s.StartTime >= from && s.EndTime <= to)
            .ToListAsync();

        return shifts.Sum(s => (int)(s.EndTime - s.StartTime).TotalHours);
    }

    public async Task<int> GetConsecutiveDaysAsync(Guid employeeId, DateTime asOf)
    {
        var shifts = await _db.Shifts
            .Where(s => s.EmployeeId == employeeId && s.EndTime <= asOf)
            .OrderByDescending(s => s.StartTime)
            .ToListAsync();

        if (shifts.Count == 0) return 0;

        var consecutiveDays = 1;
        var lastDate = shifts[0].StartTime.Date;

        for (int i = 1; i < shifts.Count; i++)
        {
            var dateDiff = (lastDate - shifts[i].StartTime.Date).Days;
            if (dateDiff <= 1)
            {
                consecutiveDays++;
                lastDate = shifts[i].StartTime.Date;
            }
            else break;
        }

        return consecutiveDays;
    }
}
