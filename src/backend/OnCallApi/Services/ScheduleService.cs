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

    public async Task<List<TimeOff>> GetTimeOffForCurrentUserAsync(string azureAdObjectId)
    {
        var employee = await _db.Employees
            .FirstOrDefaultAsync(e => e.AzureAdObjectId == azureAdObjectId);

        if (employee == null)
            return new List<TimeOff>();

        return await _db.TimeOffs
            .Where(t => t.EmployeeId == employee.Id)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<TimeOff>> GetTimeOffAsync(Guid employeeId)
    {
        return await _db.TimeOffs
            .Where(t => t.EmployeeId == employeeId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<Schedule> UpdateScheduleAsync(Schedule schedule)
    {
        var existing = await _db.Schedules.FindAsync(schedule.Id)
            ?? throw new KeyNotFoundException($"Schedule {schedule.Id} not found");

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
        var schedule = await _db.Schedules.FindAsync(id)
            ?? throw new KeyNotFoundException($"Schedule {id} not found");

        _db.Schedules.Remove(schedule);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Deleted schedule {Id}: {Name}", id, schedule.Name);
    }

    public async Task<List<Shift>> GenerateShiftsAsync(int scheduleId, int weeks)
    {
        var schedule = await _db.Schedules
            .Include(s => s.Shifts)
            .FirstOrDefaultAsync(s => s.Id == scheduleId)
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
        var startDate = DateTime.UtcNow.Date;
        var endDate = startDate.AddDays(weeks * 7);
        var tiers = new[] { "primary", "secondary", "tertiary" };

        // Generate 24-hour shifts for each day, rotating through employees
        for (var day = startDate; day < endDate; day = day.AddDays(1))
        {
            // Skip existing shifts for this date
            if (schedule.Shifts.Any(s => s.StartTime.Date == day.Date))
                continue;

            // 7a-7p day shift, 7p-7a night shift
            var shiftsForDay = new[]
            {
                new { Start = day.AddHours(7), End = day.AddHours(19), Tier = 0 },
                new { Start = day.AddHours(19), End = day.AddDays(1).AddHours(7), Tier = 1 },
            };

            foreach (var shiftDef in shiftsForDay)
            {
                var empIndex = (generatedShifts.Count + day.DayOfYear) % employees.Count;
                var employee = employees[empIndex];

                generatedShifts.Add(new Shift
                {
                    ScheduleId = scheduleId,
                    EmployeeId = employee.Id,
                    StartTime = shiftDef.Start,
                    EndTime = shiftDef.End,
                    Tier = tiers[shiftDef.Tier],
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
        _db.TimeOffs.Add(timeOff);
        await _db.SaveChangesAsync();
        return timeOff;
    }
}
