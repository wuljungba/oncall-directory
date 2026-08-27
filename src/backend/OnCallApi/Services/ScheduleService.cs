using ValidationException = System.ComponentModel.DataAnnotations.ValidationException;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

public class ScheduleService : IScheduleService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ScheduleService> _logger;
    private readonly ITeamsNotificationService? _teams;
    private readonly ITenantScope _scope;
    private readonly SchedulingOptions _scheduling;

    public ScheduleService(
        AppDbContext db,
        ILogger<ScheduleService> logger,
        ITenantScope scope,
        ITeamsNotificationService? teams = null,
        IOptions<SchedulingOptions>? scheduling = null)
    {
        _db = db;
        _logger = logger;
        _teams = teams;
        _scope = scope;
        _scheduling = scheduling?.Value ?? new SchedulingOptions();
    }

    /// <summary>The caller's tenants, or null when the query should not be restricted.</summary>
    private Task<List<int>?> AllowedTenantsAsync() => _scope.AllowedTenantIdsAsync();

    /// <summary>
    /// Schedules belong to a tenant through their department. A caller with no tenants
    /// sees nothing; the filter is never skipped.
    /// </summary>
    private async Task<IQueryable<Schedule>> ScopeSchedulesAsync(IQueryable<Schedule> query)
    {
        var tenantIds = await AllowedTenantsAsync();
        if (tenantIds == null) return query;
        return query.Where(s => s.Department != null
            && s.Department.TenantId.HasValue
            && tenantIds.Contains(s.Department.TenantId.Value));
    }

    private async Task<IQueryable<Shift>> ScopeShiftsAsync(IQueryable<Shift> query)
    {
        var tenantIds = await AllowedTenantsAsync();
        if (tenantIds == null) return query;
        return query.Where(sh => sh.Schedule.Department != null
            && sh.Schedule.Department.TenantId.HasValue
            && tenantIds.Contains(sh.Schedule.Department.TenantId.Value));
    }

    /// <summary>
    /// Resolves a schedule the caller is allowed to touch, or throws.
    ///
    /// Reads went through ScopeSchedulesAsync but every write used a bare FindAsync, so a
    /// scheduler in one tenant could rename, repoint, deactivate or delete another
    /// hospital's on-call schedule -- and the on-call schedule decides who gets paged.
    /// Same reads-scoped-writes-unscoped shape that was fixed on phone trees.
    /// </summary>
    private async Task<Schedule> RequireScheduleAsync(int scheduleId)
    {
        var scoped = await ScopeSchedulesAsync(_db.Schedules.Where(s => s.Id == scheduleId));
        return await scoped.FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException($"Schedule {scheduleId} not found");
    }

    /// <summary>Resolves a shift the caller may touch, via its schedule's tenant.</summary>
    private async Task<Shift> RequireShiftAsync(int shiftId)
    {
        var scoped = await ScopeShiftsAsync(_db.Shifts.Where(sh => sh.Id == shiftId));
        return await scoped.FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException($"Shift {shiftId} not found");
    }

    /// <summary>
    /// Confirms a department the caller may file a schedule under. A schedule reaches its
    /// tenant only through its department, so a null department would create a schedule no
    /// scoped read can ever see again.
    /// </summary>
    private async Task EnsureDepartmentAllowedAsync(int? departmentId)
    {
        var tenantIds = await AllowedTenantsAsync();
        if (tenantIds == null) return;

        if (departmentId == null)
            throw new ValidationException("A schedule must belong to a department.");

        var allowed = await _db.Departments.AnyAsync(d => d.Id == departmentId.Value
            && d.TenantId.HasValue && tenantIds.Contains(d.TenantId.Value));
        if (!allowed)
            throw new ValidationException($"Department {departmentId.Value} does not exist.");
    }

    public async Task<List<Schedule>> GetSchedulesAsync(int? departmentId = null)
    {
        var query = _db.Schedules
            .Include(s => s.Department)
            .AsQueryable();

        if (departmentId.HasValue)
            query = query.Where(s => s.DepartmentId == departmentId.Value);

        query = await ScopeSchedulesAsync(query);
        return await query.OrderByDescending(s => s.CreatedAt).ToListAsync();
    }

    public async Task<Schedule?> GetScheduleByIdAsync(int id)
    {
        var query = await ScopeSchedulesAsync(_db.Schedules
            .Include(s => s.Department)
            .Include(s => s.Shifts).ThenInclude(sh => sh.Employee)
            .Where(s => s.Id == id));

        return await query.FirstOrDefaultAsync();
    }

    public async Task<Schedule> CreateScheduleAsync(Schedule schedule)
    {
        // A schedule must belong to a real department the caller may actually use --
        // otherwise the FK insert fails with an opaque 500, or the schedule lands inside
        // another tenant's department.
        await EnsureDepartmentAllowedAsync(schedule.DepartmentId);

        _db.Schedules.Add(schedule);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Created schedule {Name} for department {DeptId}", schedule.Name, schedule.DepartmentId);
        return schedule;
    }

    public async Task<List<Shift>> GetShiftsAsync(int scheduleId, DateTime? from = null, DateTime? to = null)
    {
        var query = _db.Shifts
            .Include(s => s.Employee)
            .Where(s => s.ScheduleId == scheduleId);

        if (from.HasValue)
            query = query.Where(s => s.StartTime >= from.Value);
        if (to.HasValue)
            query = query.Where(s => s.EndTime <= to.Value);

        query = await ScopeShiftsAsync(query);
        return await query.OrderBy(s => s.StartTime).ToListAsync();
    }

    public async Task<Shift> AssignShiftAsync(int scheduleId, Guid employeeId, DateTime start, DateTime end, string tier)
    {
        // Both foreign keys are checked here rather than at SaveChanges, where a bad id
        // surfaced as a raw DbUpdateException -- an HTTP 500 naming neither field.
        await RequireScheduleAsync(scheduleId);
        var employeeExists = await _db.Employees.AnyAsync(e => e.Id == employeeId);
        if (!employeeExists)
            throw new ValidationException($"Employee {employeeId} does not exist.");

        var shift = new Shift
        {
            ScheduleId = scheduleId,
            EmployeeId = employeeId,
            StartTime = start,
            EndTime = end,
            Tier = tier,
            Status = "scheduled"
        };

        _db.Shifts.Add(shift);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Assigned shift to employee {EmpId} on schedule {ScheduleId}", employeeId, scheduleId);

        // Notify via Teams
        if (_teams != null)
        {
            var employee = await _db.Employees.FindAsync(employeeId);
            if (employee != null && !string.IsNullOrEmpty(employee.AzureAdObjectId))
            {
                _ = _teams.SendShiftStartingAsync(
                    employee.AzureAdObjectId,
                    $"{employee.FirstName} {employee.LastName}",
                    tier,
                    start,
                    $"Schedule {scheduleId}");
            }
        }

        return shift;
    }

    public async Task<ShiftSwap> RequestSwapAsync(int shiftId, Guid requestedById, Guid? replacementUserId, string reason)
    {
        // Scoped, so a swap cannot be filed against another tenant's shift, and an
        // unknown id fails as a 404 rather than an FK violation at SaveChanges.
        await RequireShiftAsync(shiftId);

        var swap = new ShiftSwap
        {
            OriginalShiftId = shiftId,
            RequestedById = requestedById,
            ReplacementUserId = replacementUserId,
            Reason = reason,
            Status = "pending"
        };

        _db.ShiftSwaps.Add(swap);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Shift swap requested for shift {ShiftId} by {EmpId}", shiftId, requestedById);
        return swap;
    }

    public async Task<ShiftSwap> ApproveSwapAsync(int swapId, Guid approvedById)
    {
        var swap = await _db.ShiftSwaps
            .Include(s => s.OriginalShift)
            .FirstOrDefaultAsync(s => s.Id == swapId)
            ?? throw new KeyNotFoundException($"Swap {swapId} not found");

        // Approving reassigns who is on call, so it must be a shift the caller may touch.
        await RequireShiftAsync(swap.OriginalShiftId);

        swap.Status = "approved";
        swap.ApprovedById = approvedById;
        swap.ApprovedAt = DateTime.UtcNow;

        // Update the original shift
        swap.OriginalShift.Status = "swapped";
        if (swap.ReplacementUserId.HasValue)
            swap.OriginalShift.EmployeeId = swap.ReplacementUserId.Value;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Shift swap {SwapId} approved by {ApproverId}", swapId, approvedById);

        // Notify requester via Teams
        if (_teams != null)
        {
            var requester = await _db.Employees.FindAsync(swap.RequestedById);
            if (requester != null && !string.IsNullOrEmpty(requester.AzureAdObjectId))
            {
                _ = _teams.SendSwapApprovedAsync(
                    requester.AzureAdObjectId,
                    $"{requester.FirstName} {requester.LastName}",
                    $"Swap {swapId}");
            }
        }

        return swap;
    }

    /// <summary>
    /// The hospital's time zone, falling back to UTC if the configured id is unknown.
    /// A bad value must not stop a schedule being generated, but it must be visible.
    /// </summary>
    private TimeZoneInfo ResolveScheduleTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(_scheduling.TimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _logger.LogError(ex,
                "Scheduling:TimeZone '{TimeZone}' is not a recognised time zone — generating shifts in UTC, "
                + "which will put them at the wrong time of day. Set a valid IANA or Windows id.",
                _scheduling.TimeZone);
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Converts a local wall-clock time to UTC for storage, handling daylight saving.
    /// On the spring-forward gap the nominal time does not exist, so the equivalent
    /// instant after the jump is used rather than throwing mid-generation.
    /// </summary>
    private static DateTime ToUtc(DateTime localTime, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    public async Task<Shift?> AcknowledgeShiftAsync(int shiftId, Guid acknowledgedById, bool isAdmin)
    {
        // Scoped: acknowledging is the signal the escalation engine trusts, so it must not
        // be possible to silence another tenant's shift.
        var scoped = await ScopeShiftsAsync(_db.Shifts.Where(s => s.Id == shiftId));
        var shift = await scoped.FirstOrDefaultAsync();
        if (shift == null) return null;

        // Only the person actually on call can confirm coverage — otherwise the signal the
        // escalation engine relies on could be silenced by anyone. Admins may acknowledge
        // on someone's behalf (a phone call to the unit answers the same question).
        if (!isAdmin && shift.EmployeeId != acknowledgedById)
            throw new UnauthorizedAccessException("Only the shift holder or an administrator may acknowledge this shift.");

        // Idempotent: re-acknowledging keeps the original confirmation time.
        if (shift.AcknowledgedAt == null)
        {
            shift.AcknowledgedAt = DateTime.UtcNow;
            shift.AcknowledgedById = acknowledgedById;
            shift.UpdatedAt = DateTime.UtcNow;

            // Any escalation already chasing this shift is now answered.
            var open = await _db.EscalationEvents
                .Where(e => e.ShiftId == shiftId && e.Status == "pending")
                .ToListAsync();
            foreach (var e in open)
            {
                e.Status = "acknowledged";
                e.ResolvedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();
            _logger.LogInformation("Shift {ShiftId} acknowledged by {EmployeeId}", shiftId, acknowledgedById);
        }

        return shift;
    }

    public async Task<List<Shift>> GetCurrentOnCallAsync(int? departmentId = null)
    {
        var now = DateTime.UtcNow;
        var query = _db.Shifts
            .Include(s => s.Employee).ThenInclude(e => e!.Department)
            .Include(s => s.Schedule)
            .Where(s => s.StartTime <= now && s.EndTime >= now && s.Status != "gap");

        if (departmentId.HasValue)
            query = query.Where(s => s.Schedule.DepartmentId == departmentId.Value);

        query = await ScopeShiftsAsync(query);
        return await query.OrderBy(s => s.Tier).ToListAsync();
    }

    public async Task<List<TimeOff>> GetTimeOffForCurrentUserAsync(string azureAdObjectId)
    {
        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.AzureAdObjectId == azureAdObjectId);

        if (employee == null)
            return new List<TimeOff>();

        return await _db.TimeOffs
            .Include(t => t.ApprovedBy)
            .Where(t => t.EmployeeId == employee.Id)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<TimeOff>> GetTimeOffAsync(Guid employeeId)
    {
        return await _db.TimeOffs
            .Include(t => t.ApprovedBy)
            .Where(t => t.EmployeeId == employeeId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    /// <summary>Pending time-off requested by an employee whose manager is the given employee.</summary>
    public async Task<List<TimeOff>> GetPendingTimeOffForManagerAsync(Guid managerEmployeeId)
    {
        return await _db.TimeOffs
            .Include(t => t.Employee)
            .Include(t => t.ApprovedBy)
            .Where(t => t.Status == "pending"
                && t.Employee != null
                && t.Employee.ManagerId == managerEmployeeId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<Schedule> UpdateScheduleAsync(Schedule schedule)
    {
        var existing = await RequireScheduleAsync(schedule.Id);
        // Guard the destination too, so an update cannot move a schedule to another tenant.
        await EnsureDepartmentAllowedAsync(schedule.DepartmentId);

        existing.Name = schedule.Name;
        existing.DepartmentId = schedule.DepartmentId;
        existing.RotationType = schedule.RotationType;
        existing.StartDate = schedule.StartDate;
        existing.EndDate = schedule.EndDate;
        existing.Notes = schedule.Notes;
        existing.IsActive = schedule.IsActive;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Updated schedule {Id}: {Name}", existing.Id, existing.Name);
        return existing;
    }

    public async Task DeleteScheduleAsync(int id)
    {
        var schedule = await RequireScheduleAsync(id);

        _db.Schedules.Remove(schedule);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Deleted schedule {Id}: {Name}", id, schedule.Name);
    }

    public async Task<List<Shift>> GenerateShiftsAsync(int scheduleId, int weeks)
    {
        var scopedSchedules = await ScopeSchedulesAsync(_db.Schedules.Where(s => s.Id == scheduleId));
        var schedule = await scopedSchedules
            .Include(s => s.Shifts)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException($"Schedule {scheduleId} not found");

        // Find employees in the same department to auto-assign
        var employees = await _db.Employees
            .Where(e => e.DepartmentId == schedule.DepartmentId && e.IsActive)
            .ToListAsync();

        if (employees.Count == 0)
        {
            _logger.LogWarning("No active employees found for department {DeptId} to generate shifts", schedule.DepartmentId);
            return [];
        }

        var generatedShifts = new List<Shift>();
        var zone = ResolveScheduleTimeZone();

        // Work in the hospital's local calendar, then store UTC. Building from
        // DateTime.UtcNow.Date meant "7am" was 07:00 UTC — 2am Eastern.
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
        var dayCount = weeks * 7;

        // Which local dates already have shifts, compared in the same calendar the shifts
        // are generated in.
        var existingLocalDates = schedule.Shifts
            .Select(s => TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(s.StartTime, DateTimeKind.Utc), zone).Date)
            .ToHashSet();

        for (var dayIndex = 0; dayIndex < dayCount; dayIndex++)
        {
            var localDay = localToday.AddDays(dayIndex);
            if (existingLocalDates.Contains(localDay))
                continue;

            // Both windows are PRIMARY cover. The night shift used to be created as
            // "secondary", which left no primary on call between 19:00 and 07:00 — and
            // code-call SMS resolution looks specifically for the primary, so every
            // overnight dispatch failed to find anyone.
            var dayStart = localDay.AddHours(_scheduling.DayShiftStartHour);
            var nightStart = localDay.AddHours(_scheduling.NightShiftStartHour);
            var nextDayStart = localDay.AddDays(1).AddHours(_scheduling.DayShiftStartHour);

            var windows = new[]
            {
                (Start: dayStart, End: nightStart),
                (Start: nightStart, End: nextDayStart),
            };

            for (var slot = 0; slot < windows.Length; slot++)
            {
                // Straight round-robin across the whole run. The previous index mixed
                // generatedShifts.Count with DayOfYear, and since the count advances twice
                // per day it skipped every other person.
                var employee = employees[(dayIndex * windows.Length + slot) % employees.Count];

                generatedShifts.Add(new Shift
                {
                    ScheduleId = scheduleId,
                    EmployeeId = employee.Id,
                    StartTime = ToUtc(windows[slot].Start, zone),
                    EndTime = ToUtc(windows[slot].End, zone),
                    Tier = "primary",
                    Status = "scheduled"
                });
            }
        }

        _db.Shifts.AddRange(generatedShifts);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Generated {Count} shifts for schedule {ScheduleId} over {Weeks} weeks",
            generatedShifts.Count, scheduleId, weeks);

        return generatedShifts;
    }

    public async Task<TimeOff> RequestTimeOffAsync(TimeOff timeOff)
    {
        // Server-side: a new request always starts pending and never carries an approver
        // or reason, and never trusts client-supplied Id/status/approver fields — closing
        // the self-approval hole where a caller could POST { status: "approved", ... }.
        timeOff.Id = 0;
        timeOff.Status = "pending";
        timeOff.ApprovedById = null;
        timeOff.ApprovalReason = null;
        timeOff.CreatedAt = DateTime.UtcNow;
        timeOff.UpdatedAt = DateTime.UtcNow;

        await ValidateNoOverlapAsync(timeOff.EmployeeId, timeOff.StartDate, timeOff.EndDate, null);
        _db.TimeOffs.Add(timeOff);
        await _db.SaveChangesAsync();
        return timeOff;
    }

    public async Task<TimeOff> UpdateTimeOffAsync(int id, TimeOffUpdateRequest request, Guid requesterId)
    {
        var timeOff = await _db.TimeOffs.FindAsync(id)
            ?? throw new KeyNotFoundException($"Time-off {id} not found");

        if (timeOff.EmployeeId != requesterId)
            throw new InvalidOperationException("You can only edit your own time-off requests.");

        if (timeOff.Status != "pending")
            throw new InvalidOperationException("Only pending requests can be edited.");

        await ValidateNoOverlapAsync(timeOff.EmployeeId, request.StartDate, request.EndDate, id);

        timeOff.StartDate = request.StartDate;
        timeOff.EndDate = request.EndDate;
        timeOff.Type = request.Type;
        timeOff.Notes = request.Notes;
        timeOff.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Updated time-off {Id} for employee {EmpId}", id, requesterId);
        return timeOff;
    }

    public async Task CancelTimeOffAsync(int id, Guid requesterId)
    {
        var timeOff = await _db.TimeOffs.FindAsync(id)
            ?? throw new KeyNotFoundException($"Time-off {id} not found");

        if (timeOff.EmployeeId != requesterId)
            throw new InvalidOperationException("You can only cancel your own time-off requests.");

        if (timeOff.Status != "pending")
            throw new InvalidOperationException("Only pending requests can be cancelled.");

        _db.TimeOffs.Remove(timeOff);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Cancelled time-off {Id} for employee {EmpId}", id, requesterId);
    }

    public async Task<TimeOff> ApproveTimeOffAsync(int id, Guid? approvedById, string? reason = null)
    {
        var timeOff = await _db.TimeOffs.FindAsync(id)
            ?? throw new KeyNotFoundException($"Time-off {id} not found");

        if (timeOff.Status != "pending")
            throw new InvalidOperationException("Only pending requests can be approved.");

        timeOff.Status = "approved";
        timeOff.ApprovedById = approvedById;
        timeOff.ApprovalReason = reason;
        timeOff.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // Immediate schedule change: approved time-off means the employee is unavailable,
        // so mark their scheduled shifts in the window as open gaps (coverage needed).
        // A scheduler/manager can then fill the gap; the on-call endpoint excludes gaps.
        var affected = await _db.Shifts
            .Where(s => s.EmployeeId == timeOff.EmployeeId
                && s.Status == "scheduled"
                && s.StartTime >= timeOff.StartDate
                && s.StartTime < timeOff.EndDate.AddDays(1))
            .ToListAsync();
        foreach (var s in affected) s.Status = "gap";
        if (affected.Count > 0)
        {
            await _db.SaveChangesAsync();
            _logger.LogInformation("Marked {Count} shifts as gap for approved time-off {Id}", affected.Count, id);
        }

        _logger.LogInformation("Approved time-off {Id} by approver {ApproverId} (reason: {Reason})",
            id, approvedById, reason ?? "-");
        return timeOff;
    }

    public async Task<TimeOff> DenyTimeOffAsync(int id, Guid? approvedById, string? reason = null)
    {
        var timeOff = await _db.TimeOffs.FindAsync(id)
            ?? throw new KeyNotFoundException($"Time-off {id} not found");

        if (timeOff.Status != "pending")
            throw new InvalidOperationException("Only pending requests can be denied.");

        timeOff.Status = "denied";
        timeOff.ApprovedById = approvedById;
        timeOff.ApprovalReason = reason;
        timeOff.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Denied time-off {Id} by approver {ApproverId} reason: {Reason}",
            id, approvedById, reason ?? "-");
        return timeOff;
    }

    public async Task<List<TimeOff>> GetAllTimeOffAsync(string? statusFilter = null)
    {
        var query = _db.TimeOffs
            .Include(t => t.Employee)
            .Include(t => t.ApprovedBy)
            .AsQueryable();

        if (!string.IsNullOrEmpty(statusFilter))
            query = query.Where(t => t.Status == statusFilter);

        return await query.OrderByDescending(t => t.CreatedAt).ToListAsync();
    }

    private async Task ValidateNoOverlapAsync(Guid employeeId, DateTime start, DateTime end, int? excludeId = null)
    {
        var query = _db.TimeOffs
            .Where(t => t.EmployeeId == employeeId && t.Status == "approved");

        if (excludeId.HasValue)
            query = query.Where(t => t.Id != excludeId.Value);

        var existing = await query.ToListAsync();

        var overlap = existing.Any(t => start < t.EndDate && end > t.StartDate);
        if (overlap)
            throw new InvalidOperationException("This time-off request overlaps with an existing approved time-off period.");
    }
}
