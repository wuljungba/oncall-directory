using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

public class ScheduleService : IScheduleService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ScheduleService> _logger;

    public ScheduleService(AppDbContext db, ILogger<ScheduleService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<Schedule>> GetSchedulesAsync(int? departmentId = null)
    {
        var query = _db.Schedules
            .Include(s => s.Department)
            .AsQueryable();

        if (departmentId.HasValue)
            query = query.Where(s => s.DepartmentId == departmentId.Value);

        return await query.OrderByDescending(s => s.CreatedAt).ToListAsync();
    }

    public async Task<Schedule?> GetScheduleByIdAsync(int id)
    {
        return await _db.Schedules
            .Include(s => s.Department)
            .Include(s => s.Shifts).ThenInclude(sh => sh.Employee)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<Schedule> CreateScheduleAsync(Schedule schedule)
    {
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

        return await query.OrderBy(s => s.StartTime).ToListAsync();
    }

    public async Task<Shift> AssignShiftAsync(int scheduleId, Guid employeeId, DateTime start, DateTime end, string tier)
    {
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
        return shift;
    }

    public async Task<ShiftSwap> RequestSwapAsync(int shiftId, Guid requestedById, Guid? replacementUserId, string reason)
    {
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

        swap.Status = "approved";
        swap.ApprovedById = approvedById;
        swap.ApprovedAt = DateTime.UtcNow;

        // Update the original shift
        swap.OriginalShift.Status = "swapped";
        if (swap.ReplacementUserId.HasValue)
            swap.OriginalShift.EmployeeId = swap.ReplacementUserId.Value;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Shift swap {SwapId} approved by {ApproverId}", swapId, approvedById);
        return swap;
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

        return await query.OrderBy(s => s.Tier).ToListAsync();
    }

    public async Task<List<TimeOff>> GetTimeOffAsync(Guid employeeId)
    {
        return await _db.TimeOffs
            .Where(t => t.EmployeeId == employeeId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<TimeOff> RequestTimeOffAsync(TimeOff timeOff)
    {
        _db.TimeOffs.Add(timeOff);
        await _db.SaveChangesAsync();
        return timeOff;
    }
}
