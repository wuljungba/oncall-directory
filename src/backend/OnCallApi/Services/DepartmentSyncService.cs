using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Background service that syncs M365 Groups to departments automatically.
/// Runs on startup and periodically thereafter (default: every 6 hours).
/// Maps each M365 Group to a department record, and group members to department employees.
/// </summary>
public class DepartmentSyncService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DepartmentSyncService> _logger;
    private readonly int _intervalMinutes;

    public DepartmentSyncService(IServiceProvider services, IConfiguration config, ILogger<DepartmentSyncService> logger)
    {
        _services = services;
        _logger = logger;
        _intervalMinutes = config.GetValue<int>("Sync:DepartmentSyncIntervalMinutes", 360);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run initial sync after a short delay to let the app initialize
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        await SyncDepartmentsAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_intervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SyncDepartmentsAsync(stoppingToken);
        }
    }

    private async Task SyncDepartmentsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var graphApi = scope.ServiceProvider.GetRequiredService<IGraphApiService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            _logger.LogInformation("Starting M365 department sync");

            var groups = await graphApi.GetAllGroupsAsync(ct);
            _logger.LogInformation("Found {Count} M365 groups", groups.Count);

            foreach (var group in groups)
            {
                // Create or update department from group
                var existing = await db.Departments
                    .FirstOrDefaultAsync(d => d.AzureAdGroupId == group.Id || d.Name == group.Name, ct);

                if (existing != null)
                {
                    existing.Name = group.Name;
                    existing.AzureAdGroupId = group.Id;
                    existing.IsActive = true;
                }
                else
                {
                    db.Departments.Add(new Department
                    {
                        Name = group.Name,
                        AzureAdGroupId = group.Id,
                        Description = $"Auto-created from M365 Group: {group.Name}",
                        IsActive = true,
                    });
                }

                // Sync group members to department employees
                var members = await graphApi.GetDepartmentMembersAsync(group.Id, ct);
                foreach (var member in members)
                {
                    if (string.IsNullOrEmpty(member.AzureAdObjectId)) continue;

                    var employee = await db.Employees
                        .FirstOrDefaultAsync(e => e.AzureAdObjectId == member.AzureAdObjectId, ct);

                    if (employee != null)
                    {
                        var dept = await db.Departments
                            .FirstOrDefaultAsync(d => d.AzureAdGroupId == group.Id, ct);
                        if (dept != null) employee.DepartmentId = dept.Id;
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("M365 department sync completed: {Count} groups processed", groups.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "M365 department sync failed");
        }
    }
}
