using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
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
        // 0 or less means disabled, as it does for AD sync. Without this gate the service had
        // no off switch at all: it ran its first cycle 30 seconds after startup on every
        // machine, so a local run authenticated to the configured tenant and pulled a real
        // directory onto a developer's laptop.
        if (_intervalMinutes <= 0)
        {
            _logger.LogInformation("Department sync is disabled (Sync:DepartmentSyncIntervalMinutes <= 0)");
            return;
        }

        // Run initial sync after a short delay to let the app initialize
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        await SyncDepartmentsAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_intervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SyncDepartmentsAsync(stoppingToken);
        }
    }

    /// <summary>
    /// One cycle across every connected directory.
    ///
    /// It used to read our own directory only, so a customer's groups were never seen, and it
    /// matched departments by name across the whole estate — two hospitals with a "Cardiology"
    /// group collapsed onto one row, and whichever synced last took the group id.
    /// </summary>
    private async Task SyncDepartmentsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var graphApi = scope.ServiceProvider.GetRequiredService<IGraphApiService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var graphOptions = scope.ServiceProvider.GetRequiredService<IOptions<GraphApiOptions>>();

            var targets = await DirectorySyncTargets.ResolveAsync(db, graphOptions.Value.TenantId, ct);

            foreach (var target in targets)
            {
                try
                {
                    await SyncDirectoryAsync(graphApi, db, target, ct);
                }
                catch (Exception ex)
                {
                    // One customer's directory being unreachable must not stop the rest.
                    _logger.LogError(ex, "Department sync failed for {Directory}", target.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "M365 department sync failed");
        }
    }

    private async Task SyncDirectoryAsync(
        IGraphApiService graphApi, AppDbContext db, DirectoryTarget target, CancellationToken ct)
    {
        var groups = await graphApi.GetAllGroupsAsync(target.EntraTenantId, ct);

        if (!groups.Completed)
        {
            // Worth saying, and not worth stopping for: everything below creates or updates, and
            // nothing here deactivates a department that has gone missing. A partial read means
            // some groups are not seen this cycle, not that any of them ended.
            _logger.LogWarning(
                "Only part of {Directory}'s group list could be read ({Detail}); "
                + "processing the {Count} group(s) that were",
                target.Name, groups.FailureDetail, groups.Groups.Count);
        }

        foreach (var group in groups.Groups)
        {
            // Scoped to this directory's tenant. Matching estate-wide let one customer's group
            // adopt another customer's department row.
            var existing = await db.Departments.FirstOrDefaultAsync(
                d => d.TenantId == target.TenantId
                    && (d.AzureAdGroupId == group.Id || d.Name == group.Name), ct);

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
                    // Set on create only. An existing department with no tenant may be
                    // deliberately global — the seeded ones are — and narrowing one to a tenant
                    // would quietly remove it from everyone else's view.
                    TenantId = target.TenantId,
                    IsActive = true,
                });
            }

            await db.SaveChangesAsync(ct);

            var members = await graphApi.GetDepartmentMembersAsync(target.EntraTenantId, group.Id, ct);
            if (!members.Completed)
            {
                _logger.LogWarning(
                    "Only part of the membership of group {GroupName} in {Directory} could be read ({Detail})",
                    group.Name, target.Name, members.FailureDetail);
            }

            var department = await db.Departments.FirstOrDefaultAsync(
                d => d.TenantId == target.TenantId && d.AzureAdGroupId == group.Id, ct);
            if (department == null) continue;

            foreach (var member in members.Members)
            {
                if (string.IsNullOrEmpty(member.AzureAdObjectId)) continue;

                // Only people this directory owns. An object id from one tenant must never move
                // another tenant's employee into a department.
                var employee = await db.Employees.FirstOrDefaultAsync(
                    e => e.TenantId == target.TenantId && e.AzureAdObjectId == member.AzureAdObjectId, ct);

                if (employee != null) employee.DepartmentId = department.Id;
            }

            await db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Department sync for {Directory}: {Count} group(s) processed", target.Name, groups.Groups.Count);
    }
}
