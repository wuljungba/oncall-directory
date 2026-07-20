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
}
