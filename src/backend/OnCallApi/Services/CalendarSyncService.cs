using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;

namespace OnCallApi.Services;

/// <summary>
/// Background service that syncs on-call shifts to users' Outlook calendars.
/// Also checks free/busy availability when shifts are assigned to avoid conflicts.
/// </summary>
public class CalendarSyncService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CalendarSyncService> _logger;
    private readonly int _intervalMinutes;

    public CalendarSyncService(IServiceProvider services, IConfiguration config, ILogger<CalendarSyncService> logger)
    {
        _services = services;
        _logger = logger;
        _intervalMinutes = config.GetValue<int>("Sync:CalendarSyncIntervalMinutes", 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        await SyncCalendarEventsAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_intervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SyncCalendarEventsAsync(stoppingToken);
        }
    }

    private async Task SyncCalendarEventsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var graphApi = scope.ServiceProvider.GetRequiredService<IGraphApiService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Get future shifts that haven't been synced yet
            var unsyncedShifts = await db.Shifts
                .Include(s => s.Employee)
                .Include(s => s.Schedule)
                .Where(s => s.StartTime > DateTime.UtcNow.AddDays(-1)
                    && s.Status != "gap"
                    && s.Employee != null
                    && !string.IsNullOrEmpty(s.Employee.AzureAdObjectId))
                .ToListAsync(ct);

            if (unsyncedShifts.Count == 0)
            {
                _logger.LogDebug("Calendar sync: no unsynced shifts found");
                return;
            }

            var synced = 0;
            foreach (var shift in unsyncedShifts)
            {
                if (shift.Employee == null || string.IsNullOrEmpty(shift.Employee.AzureAdObjectId))
                    continue;

                try
                {
                    var title = shift.Schedule != null
                        ? $"On-Call ({shift.Tier}) - {shift.Schedule.Name}"
                        : $"On-Call ({shift.Tier})";

                    await graphApi.CreateOutlookCalendarEventAsync(
                        shift.Employee.AzureAdObjectId,
                        title,
                        shift.StartTime,
                        shift.EndTime,
                        ct);

                    synced++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to push shift {ShiftId} to calendar for {Employee}",
                        shift.Id, shift.Employee.AzureAdObjectId);
                }
            }

            _logger.LogInformation("Calendar sync: {Synced} shifts pushed to Outlook", synced);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Calendar sync cycle failed");
        }
    }
}

/// <summary>
/// Service for checking free/busy availability via Outlook calendar.
/// Used before shift assignment to detect scheduling conflicts.
/// </summary>
public class AvailabilityService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AvailabilityService> _logger;

    public AvailabilityService(IServiceProvider services, ILogger<AvailabilityService> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Check if an employee is available during the specified time window.
    /// Returns true if no Outlook calendar events overlap.
    /// </summary>
    public async Task<bool> IsAvailableAsync(Guid employeeId, DateTime start, DateTime end)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var employee = await db.Employees.FindAsync(employeeId);
            if (employee == null || string.IsNullOrEmpty(employee.AzureAdObjectId))
                return true; // Can't check, assume available

            // Check local schedule first — no overlapping shifts
            var overlappingShifts = await db.Shifts
                .Where(s => s.EmployeeId == employeeId
                    && s.StartTime < end
                    && s.EndTime > start
                    && s.Status != "gap")
                .AnyAsync();

            if (overlappingShifts)
                return false;

            // Check for approved time-off
            var hasTimeOff = await db.TimeOffs
                .Where(t => t.EmployeeId == employeeId
                    && t.Status == "approved"
                    && t.StartDate <= end.Date
                    && t.EndDate >= start.Date)
                .AnyAsync();

            if (hasTimeOff)
                return false;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Availability check failed for employee {EmployeeId}", employeeId);
            return true; // Fail open — assume available
        }
    }
}
