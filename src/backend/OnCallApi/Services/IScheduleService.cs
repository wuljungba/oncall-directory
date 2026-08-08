using OnCallApi.Models;

namespace OnCallApi.Services;

public interface IScheduleService
{
    Task<List<Schedule>> GetSchedulesAsync(int? departmentId = null);
    Task<Schedule?> GetScheduleByIdAsync(int id);
    Task<Schedule> CreateScheduleAsync(Schedule schedule);
    Task<List<Shift>> GetShiftsAsync(int scheduleId, DateTime? from = null, DateTime? to = null);
    Task<Shift> AssignShiftAsync(int scheduleId, Guid employeeId, DateTime start, DateTime end, string tier);
    Task<ShiftSwap> RequestSwapAsync(int shiftId, Guid requestedById, Guid? replacementUserId, string reason);
    Task<ShiftSwap> ApproveSwapAsync(int swapId, Guid approvedById);
    Task<List<Shift>> GetCurrentOnCallAsync(int? departmentId = null);
    Task<List<TimeOff>> GetTimeOffAsync(Guid employeeId);
    Task<List<TimeOff>> GetTimeOffForCurrentUserAsync(string azureAdObjectId);
    Task<TimeOff> RequestTimeOffAsync(TimeOff timeOff);
    Task<TimeOff> UpdateTimeOffAsync(int id, TimeOffUpdateRequest request, Guid requesterId);
    Task CancelTimeOffAsync(int id, Guid requesterId);
    Task<TimeOff> ApproveTimeOffAsync(int id, Guid? approvedById, string? reason = null);
    Task<TimeOff> DenyTimeOffAsync(int id, Guid? approvedById, string? reason = null);
    Task<List<TimeOff>> GetPendingTimeOffForManagerAsync(Guid managerEmployeeId);
    Task<List<TimeOff>> GetAllTimeOffAsync(string? statusFilter = null);
    Task<Schedule> UpdateScheduleAsync(Schedule schedule);
    Task DeleteScheduleAsync(int id);
    Task<List<Shift>> GenerateShiftsAsync(int scheduleId, int weeks);
}
