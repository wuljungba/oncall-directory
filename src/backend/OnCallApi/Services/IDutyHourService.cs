using OnCallApi.Models;

namespace OnCallApi.Services;

public interface IDutyHourService
{
    Task<List<DutyHourRule>> GetRulesAsync(int? departmentId = null);
    Task<DutyHourRule> CreateRuleAsync(DutyHourRule rule);
    Task<DutyHourRule> UpdateRuleAsync(DutyHourRule rule);
    Task DeleteRuleAsync(int id);
    Task<List<DutyHourViolation>> CheckComplianceAsync(Guid employeeId, DateTime? from = null, DateTime? to = null);
    Task<List<DutyHourViolation>> CheckAllComplianceAsync(DateTime? from = null, DateTime? to = null);
    Task<int> GetHoursWorkedAsync(Guid employeeId, DateTime from, DateTime to);
    Task<int> GetConsecutiveDaysAsync(Guid employeeId, DateTime asOf);
}
