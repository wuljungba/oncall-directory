using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using OnCallApi.Hubs;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "RequireViewer")]
public class ScheduleController : ControllerBase
{
    private readonly IScheduleService _scheduleService;
    private readonly IHubContext<OnCallNotificationHub> _hub;

    public ScheduleController(IScheduleService scheduleService, IHubContext<OnCallNotificationHub> hub)
    {
        _scheduleService = scheduleService;
        _hub = hub;
    }

    /// <summary>Get all schedules, optionally filtered by department.</summary>
    [HttpGet]
    public async Task<ActionResult<List<Schedule>>> GetSchedules([FromQuery] int? departmentId)
    {
        return await _scheduleService.GetSchedulesAsync(departmentId);
    }

    /// <summary>Get a single schedule with its shifts.</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Schedule>> GetSchedule(int id)
    {
        var schedule = await _scheduleService.GetScheduleByIdAsync(id);
        if (schedule == null) return NotFound();
        return schedule;
    }

    /// <summary>Create a new schedule.</summary>
    [HttpPost]
    [Authorize(Policy = "RequireScheduler")]
    public async Task<ActionResult<Schedule>> CreateSchedule(Schedule schedule)
    {
        var created = await _scheduleService.CreateScheduleAsync(schedule);

        await _hub.Clients.All.SendAsync("ScheduleCreated", created);
        return CreatedAtAction(nameof(GetSchedule), new { id = created.Id }, created);
    }

    /// <summary>Get shifts for a schedule within an optional date range.</summary>
    [HttpGet("{scheduleId}/shifts")]
    public async Task<ActionResult<List<Shift>>> GetShifts(int scheduleId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        return await _scheduleService.GetShiftsAsync(scheduleId, from, to);
    }

    /// <summary>Assign a shift to an employee.</summary>
    [HttpPost("{scheduleId}/shifts")]
    [Authorize(Policy = "RequireScheduler")]
    public async Task<ActionResult<Shift>> AssignShift(int scheduleId, [FromBody] AssignShiftRequest request)
    {
        var shift = await _scheduleService.AssignShiftAsync(
            scheduleId, request.EmployeeId, request.StartTime, request.EndTime, request.Tier);

        await _hub.Clients.All.SendAsync("ShiftAssigned", shift);
        return CreatedAtAction(nameof(GetShifts), new { scheduleId }, shift);
    }

    /// <summary>Get who's currently on call.</summary>
    [HttpGet("on-call")]
    public async Task<ActionResult<List<Shift>>> GetCurrentOnCall([FromQuery] int? departmentId)
    {
        return await _scheduleService.GetCurrentOnCallAsync(departmentId);
    }

    /// <summary>Request a shift swap.</summary>
    [HttpPost("swaps")]
    public async Task<ActionResult<ShiftSwap>> RequestSwap([FromBody] SwapRequest request)
    {
        var swap = await _scheduleService.RequestSwapAsync(
            request.ShiftId, request.RequestedById, request.ReplacementUserId, request.Reason);

        await _hub.Clients.All.SendAsync("SwapRequested", swap);
        return CreatedAtAction(nameof(RequestSwap), swap);
    }

    /// <summary>Approve a shift swap.</summary>
    [HttpPost("swaps/{id}/approve")]
    [Authorize(Policy = "RequireScheduler")]
    public async Task<ActionResult<ShiftSwap>> ApproveSwap(int id)
    {
        var userId = Guid.Parse(User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")!.Value);
        var swap = await _scheduleService.ApproveSwapAsync(id, userId);

        await _hub.Clients.All.SendAsync("SwapApproved", swap);
        return swap;
    }

    /// <summary>Get time-off for an employee.</summary>
    [HttpGet("time-off/{employeeId}")]
    public async Task<ActionResult<List<TimeOff>>> GetTimeOff(Guid employeeId)
    {
        return await _scheduleService.GetTimeOffAsync(employeeId);
    }

    /// <summary>Request time off.</summary>
    [HttpPost("time-off")]
    public async Task<ActionResult<TimeOff>> RequestTimeOff(TimeOff timeOff)
    {
        var created = await _scheduleService.RequestTimeOffAsync(timeOff);
        return CreatedAtAction(nameof(GetTimeOff), new { employeeId = timeOff.EmployeeId }, created);
    }
}

// ── Request DTOs ──

public record AssignShiftRequest(
    Guid EmployeeId,
    DateTime StartTime,
    DateTime EndTime,
    string Tier = "primary"
);

public record SwapRequest(
    int ShiftId,
    Guid RequestedById,
    Guid? ReplacementUserId,
    string Reason
);
