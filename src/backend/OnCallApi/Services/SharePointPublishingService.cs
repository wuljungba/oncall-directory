using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Publishes finalized on-call schedules to SharePoint pages.
/// Uses Microsoft Graph to create/update pages on a designated SharePoint site.
/// The target site is configured in app settings (SharePoint:SiteId).
/// </summary>
public class SharePointPublishingService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SharePointPublishingService> _logger;
    private readonly string? _siteId;

    public SharePointPublishingService(IServiceProvider services, IConfiguration config, ILogger<SharePointPublishingService> logger)
    {
        _services = services;
        _logger = logger;
        _siteId = config.GetValue<string>("SharePoint:SiteId");
    }

    /// <summary>
    /// Publish a schedule to a SharePoint page.
    /// Returns the page URL if successful, or an error message.
    /// </summary>
    public async Task<string> PublishScheduleAsync(int scheduleId)
    {
        if (string.IsNullOrEmpty(_siteId))
            return "SharePoint publishing is not configured. Set SharePoint:SiteId in app settings.";

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var graphApi = scope.ServiceProvider.GetRequiredService<IGraphApiService>();

            var schedule = await db.Schedules
                .Include(s => s.Department)
                .Include(s => s.Shifts).ThenInclude(sh => sh.Employee)
                .FirstOrDefaultAsync(s => s.Id == scheduleId);

            if (schedule == null)
                return $"Schedule {scheduleId} not found.";

            // Generate SharePoint page content
            var title = $"On-Call Schedule: {schedule.Name}";
            var pageContent = GeneratePageContent(schedule);

            await graphApi.CreateSharePointPageAsync(_siteId, title, pageContent);
            var pageUrl = $"{_siteId}/SitePages/{title.Replace(" ", "-")}.aspx";

            _logger.LogInformation("Published schedule {ScheduleId} to SharePoint: {Url}", scheduleId, pageUrl);
            return $"Published successfully: {pageUrl}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish schedule {ScheduleId} to SharePoint", scheduleId);
            return $"Failed to publish: {ex.Message}";
        }
    }

    private static string GeneratePageContent(Schedule schedule)
    {
        var lines = new List<string>
        {
            $"<h1>On-Call Schedule: {schedule.Name}</h1>",
            $"<p>Department: {schedule.Department?.Name ?? "General"}</p>",
            $"<p>Period: {schedule.StartDate:MMM d, yyyy} — {schedule.EndDate:MMM d, yyyy}</p>",
            "<hr>",
            "<table><thead><tr><th>Date</th><th>Time</th><th>Tier</th><th>Assigned To</th></tr></thead><tbody>"
        };

        foreach (var shift in schedule.Shifts.OrderBy(s => s.StartTime))
        {
            var name = shift.Employee != null
                ? $"{shift.Employee.FirstName} {shift.Employee.LastName}"
                : "Unassigned";
            lines.Add($"<tr><td>{shift.StartTime:MMM d}</td><td>{shift.StartTime:h:mm tt}—{shift.EndTime:h:mm tt}</td><td>{shift.Tier}</td><td>{name}</td></tr>");
        }

        lines.Add("</tbody></table>");
        lines.Add("<hr><p><em>Auto-published from OnCall Schedule & Directory</em></p>");
        return string.Join("\n", lines);
    }
}
