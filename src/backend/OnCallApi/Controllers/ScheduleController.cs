using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using OnCallApi.Hubs;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "RequireScheduleRead")]
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
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<Schedule>> CreateSchedule(Schedule schedule)
    {
        var created = await _scheduleService.CreateScheduleAsync(schedule);

        await _hub.Clients.All.SendAsync("ScheduleCreated", created);
        return CreatedAtAction(nameof(GetSchedule), new { id = created.Id }, created);
    }

    /// <summary>Update an existing schedule.</summary>
    [HttpPut("{id}")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<Schedule>> UpdateSchedule(int id, Schedule schedule)
    {
        if (id != schedule.Id)
            return BadRequest(new { error = "Route ID and body ID must match." });

        var updated = await _scheduleService.UpdateScheduleAsync(schedule);
        return Ok(updated);
    }

    /// <summary>Delete a schedule.</summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> DeleteSchedule(int id)
    {
        await _scheduleService.DeleteScheduleAsync(id);
        return NoContent();
    }

    /// <summary>Auto-generate shifts for a schedule for N weeks.</summary>
    [HttpPost("{scheduleId}/generate")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<List<Shift>>> GenerateShifts(int scheduleId, [FromQuery] int weeks = 4)
    {
        var shifts = await _scheduleService.GenerateShiftsAsync(scheduleId, weeks);

        await _hub.Clients.All.SendAsync("ShiftsGenerated", new { scheduleId, count = shifts.Count });
        return Ok(shifts);
    }

    /// <summary>Get shifts for a schedule within an optional date range.</summary>
    [HttpGet("{scheduleId}/shifts")]
    public async Task<ActionResult<List<Shift>>> GetShifts(int scheduleId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        return await _scheduleService.GetShiftsAsync(scheduleId, from, to);
    }

    /// <summary>Assign a shift to an employee.</summary>
    [HttpPost("{scheduleId}/shifts")]
    [Authorize(Policy = "RequireScheduleWrite")]
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
            request.ShiftId, request.RequestedById, request.ReplacementUserId, request.Reason ?? string.Empty);

        await _hub.Clients.All.SendAsync("SwapRequested", swap);
        return CreatedAtAction(nameof(RequestSwap), swap);
    }

    /// <summary>Approve a shift swap.</summary>
    [HttpPost("swaps/{id}/approve")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<ShiftSwap>> ApproveSwap(int id)
    {
        var userId = Guid.Parse(User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")!.Value);
        var swap = await _scheduleService.ApproveSwapAsync(id, userId);

        await _hub.Clients.All.SendAsync("SwapApproved", swap);
        return swap;
    }

    /// <summary>Get time-off for the currently authenticated user.</summary>
    [HttpGet("time-off/me")]
    public async Task<ActionResult<List<TimeOff>>> GetMyTimeOff()
    {
        var azureAdObjectId = User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? throw new UnauthorizedAccessException("User identity not found in token.");
        return await _scheduleService.GetTimeOffForCurrentUserAsync(azureAdObjectId);
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
        await _hub.Clients.All.SendAsync("TimeOffUpdated", created);
        return CreatedAtAction(nameof(GetTimeOff), new { employeeId = timeOff.EmployeeId }, created);
    }

    /// <summary>Update/edit a pending time-off request.</summary>
    [HttpPut("time-off/{id}")]
    public async Task<ActionResult<TimeOff>> UpdateTimeOff(int id, [FromBody] TimeOffUpdateRequest request)
    {
        try
        {
            var requesterId = GetCurrentUserId();
            var updated = await _scheduleService.UpdateTimeOffAsync(id, request, requesterId);
            await _hub.Clients.All.SendAsync("TimeOffUpdated", updated);
            return Ok(updated);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Cancel/withdraw a pending time-off request.</summary>
    [HttpDelete("time-off/{id}")]
    public async Task<ActionResult> CancelTimeOff(int id)
    {
        try
        {
            var requesterId = GetCurrentUserId();
            await _scheduleService.CancelTimeOffAsync(id, requesterId);
            await _hub.Clients.All.SendAsync("TimeOffUpdated", new { id, action = "cancelled" });
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Approve a pending time-off request (admin only).</summary>
    [HttpPost("time-off/{id}/approve")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<TimeOff>> ApproveTimeOff(int id)
    {
        try
        {
            var approvedById = GetCurrentUserId();
            var approved = await _scheduleService.ApproveTimeOffAsync(id, approvedById);
            await _hub.Clients.All.SendAsync("TimeOffUpdated", approved);
            return Ok(approved);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Deny a pending time-off request (admin only).</summary>
    [HttpPost("time-off/{id}/deny")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<TimeOff>> DenyTimeOff(int id)
    {
        try
        {
            var approvedById = GetCurrentUserId();
            var denied = await _scheduleService.DenyTimeOffAsync(id, approvedById);
            await _hub.Clients.All.SendAsync("TimeOffUpdated", denied);
            return Ok(denied);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Get all time-off requests (admin view with optional status filter).</summary>
    [HttpGet("time-off/all")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<List<TimeOff>>> GetAllTimeOff([FromQuery] string? status)
    {
        return await _scheduleService.GetAllTimeOffAsync(status);
    }

    private Guid GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim == null || !Guid.TryParse(userIdClaim, out var userId))
            throw new UnauthorizedAccessException("User identity not found in token.");
        return userId;
    }
}
