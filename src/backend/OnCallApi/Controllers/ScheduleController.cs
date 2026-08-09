using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Authorization;
using OnCallApi.Data;
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
    private readonly AppDbContext _db;
    private readonly ITenantContextService _tenantContext;
    private readonly ILogger<ScheduleController> _logger;

    public ScheduleController(
        IScheduleService scheduleService,
        IHubContext<OnCallNotificationHub> hub,
        AppDbContext db,
        ITenantContextService tenantContext,
        ILogger<ScheduleController> logger)
    {
        _scheduleService = scheduleService;
        _hub = hub;
        _db = db;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    private async Task BroadcastToTenantGroupAsync(int? tenantId, string method, object arg)
    {
        if (tenantId.HasValue)
        {
            await _hub.Clients.Group($"tenant-{tenantId.Value}").SendAsync(method, arg);
        }
        else
        {
            _logger.LogWarning(
                "Could not resolve tenantId for notification '{Method}', broadcasting to all clients",
                method);
            await _hub.Clients.All.SendAsync(method, arg);
        }
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
        try
        {
            var created = await _scheduleService.CreateScheduleAsync(schedule);

            var tenantId = await _db.Departments
                .Where(d => d.Id == created.DepartmentId)
                .Select(d => d.TenantId)
                .FirstOrDefaultAsync();
            await BroadcastToTenantGroupAsync(tenantId, "ScheduleCreated", created);
            return CreatedAtAction(nameof(GetSchedule), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
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

    /// <summary>Auto-generate shifts for a schedule for N weeks (max 52).</summary>
    [HttpPost("{scheduleId}/generate")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<List<Shift>>> GenerateShifts(int scheduleId, [FromQuery] int weeks = 4)
    {
        if (weeks < 1 || weeks > 52)
            return BadRequest(new { error = "Weeks must be between 1 and 52." });

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
        // RequestedById must be the caller's own internal Employee.Id (never the client's
        // claim), matching the Employee FK — the AD oid Guid is a different namespace.
        var me = await _tenantContext.GetCurrentEmployeeIdAsync(User);
        if (!me.HasValue) return BadRequest(new { error = "No employee profile is linked to your account." });

        var swap = await _scheduleService.RequestSwapAsync(
            request.ShiftId, me.Value, request.ReplacementUserId, request.Reason ?? string.Empty);

        await _hub.Clients.All.SendAsync("SwapRequested", swap);
        return CreatedAtAction(nameof(RequestSwap), swap);
    }

    /// <summary>Approve a shift swap.</summary>
    [HttpPost("swaps/{id}/approve")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<ShiftSwap>> ApproveSwap(int id)
    {
        var approverId = await _tenantContext.GetCurrentEmployeeIdAsync(User);
        if (!approverId.HasValue) return BadRequest(new { error = "No employee profile is linked to your account." });

        var swap = await _scheduleService.ApproveSwapAsync(id, approverId.Value);

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

    /// <summary>Request time off — bound to the authenticated user's employee profile.</summary>
    [HttpPost("time-off")]
    public async Task<ActionResult<TimeOff>> RequestTimeOff(TimeOff timeOff)
    {
        var employeeId = await _tenantContext.GetCurrentEmployeeIdAsync(User);
        if (!employeeId.HasValue)
            return BadRequest(new { error = "No employee profile is linked to your account. Contact an administrator." });

        // Never trust a client-supplied EmployeeId.
        timeOff.EmployeeId = employeeId.Value;

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
            var requesterId = await _tenantContext.GetCurrentEmployeeIdAsync(User);
            if (!requesterId.HasValue) return BadRequest(new { error = "No employee profile is linked to your account." });
            var updated = await _scheduleService.UpdateTimeOffAsync(id, request, requesterId.Value);
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
            var requesterId = await _tenantContext.GetCurrentEmployeeIdAsync(User);
            if (!requesterId.HasValue) return BadRequest(new { error = "No employee profile is linked to your account." });
            await _scheduleService.CancelTimeOffAsync(id, requesterId.Value);
            await _hub.Clients.All.SendAsync("TimeOffUpdated", new { id, action = "cancelled" });
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>
    /// Approve a pending time-off request. Allowed for the requester's manager, plus
    /// any super admin or scoped tenant admin (admin fallback).
    /// </summary>
    [HttpPost("time-off/{id}/approve")]
    public async Task<ActionResult<TimeOff>> ApproveTimeOff(int id, [FromBody] TimeOffApprovalRequest? request = null)
    {
        try
        {
            var (allowed, approverId) = await ResolveApproverAsync(id);
            if (!allowed) return Forbid();
            var approved = await _scheduleService.ApproveTimeOffAsync(id, approverId, request?.Reason);
            await _hub.Clients.All.SendAsync("TimeOffUpdated", approved);
            return Ok(approved);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>
    /// Deny a pending time-off request. Allowed for the requester's manager, plus
    /// any super admin or scoped tenant admin (admin fallback).
    /// </summary>
    [HttpPost("time-off/{id}/deny")]
    public async Task<ActionResult<TimeOff>> DenyTimeOff(int id, [FromBody] TimeOffApprovalRequest? request = null)
    {
        try
        {
            var (allowed, approverId) = await ResolveApproverAsync(id);
            if (!allowed) return Forbid();
            var denied = await _scheduleService.DenyTimeOffAsync(id, approverId, request?.Reason);
            await _hub.Clients.All.SendAsync("TimeOffUpdated", denied);
            return Ok(denied);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Pending time-off requested by the current user's direct reports (manager view).</summary>
    [HttpGet("time-off/review")]
    public async Task<ActionResult<List<TimeOff>>> GetTimeOffReview()
    {
        var managerId = await _tenantContext.GetCurrentEmployeeIdAsync(User);
        if (!managerId.HasValue) return Forbid();
        return await _scheduleService.GetPendingTimeOffForManagerAsync(managerId.Value);
    }

    /// <summary>Get all time-off requests (admin view with optional status filter).</summary>
    [HttpGet("time-off/all")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult<List<TimeOff>>> GetAllTimeOff([FromQuery] string? status)
    {
        return await _scheduleService.GetAllTimeOffAsync(status);
    }

    /// <summary>
    /// Resolves who may approve/deny a request: the requester's manager, or any super
    /// admin / scoped tenant admin (admin fallback). Returns the approver's internal
    /// Employee.Id (null when a super admin has no linked profile).
    /// </summary>
    private async Task<(bool Allowed, Guid? ApproverId)> ResolveApproverAsync(int id)
    {
        var timeOff = await _db.TimeOffs
            .Include(t => t.Employee)
            .FirstOrDefaultAsync(t => t.Id == id);
        if (timeOff == null) throw new KeyNotFoundException($"Time-off {id} not found");

        if (_tenantContext.IsSuperAdmin(User)
            || User.HasClaim(Permissions.ClaimType, Permissions.AdminScoped))
        {
            // Admin fallback — record the approver's profile id if they have one.
            return (true, await _tenantContext.GetCurrentEmployeeIdAsync(User));
        }

        var approverId = await _tenantContext.GetCurrentEmployeeIdAsync(User);
        if (!approverId.HasValue) return (false, null);

        var isManager = timeOff.Employee != null && timeOff.Employee.ManagerId == approverId.Value;
        return (isManager, isManager ? approverId.Value : null);
    }
}
