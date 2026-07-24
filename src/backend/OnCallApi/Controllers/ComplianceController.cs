using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/compliance")]
[Authorize(Policy = "RequireScheduleRead")]
public class ComplianceController : ControllerBase
{
    private readonly IDutyHourService _dutyHourService;

    public ComplianceController(IDutyHourService dutyHourService)
    {
        _dutyHourService = dutyHourService;
    }

    [HttpGet("rules")]
    public async Task<ActionResult<List<DutyHourRule>>> GetRules([FromQuery] int? departmentId)
    {
        return await _dutyHourService.GetRulesAsync(departmentId);
    }

    [HttpPost("rules")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<DutyHourRule>> CreateRule(DutyHourRule rule)
    {
        var created = await _dutyHourService.CreateRuleAsync(rule);
        return CreatedAtAction(nameof(GetRules), new { id = created.Id }, created);
    }

    [HttpPut("rules/{id}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<DutyHourRule>> UpdateRule(int id, DutyHourRule rule)
    {
        if (id != rule.Id) return BadRequest();
        return await _dutyHourService.UpdateRuleAsync(rule);
    }

    [HttpDelete("rules/{id}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> DeleteRule(int id)
    {
        await _dutyHourService.DeleteRuleAsync(id);
        return NoContent();
    }

    [HttpGet("check/{employeeId}")]
    public async Task<ActionResult<List<DutyHourViolation>>> CheckEmployee(Guid employeeId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        return await _dutyHourService.CheckComplianceAsync(employeeId, from, to);
    }

    [HttpGet("check")]
    public async Task<ActionResult<List<DutyHourViolation>>> CheckAll([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        return await _dutyHourService.CheckAllComplianceAsync(from, to);
    }

    [HttpGet("hours/{employeeId}")]
    public async Task<ActionResult<int>> GetHoursWorked(Guid employeeId, [FromQuery] DateTime from, [FromQuery] DateTime to)
    {
        return await _dutyHourService.GetHoursWorkedAsync(employeeId, from, to);
    }
}
