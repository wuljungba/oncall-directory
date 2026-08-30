using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/integrations")]
[Authorize(Policy = "RequireScheduleRead")]
public class IntegrationsController : ControllerBase
{
    private readonly IGraphApiService _graphApi;
    private readonly ILogger<IntegrationsController> _logger;

    public IntegrationsController(IGraphApiService graphApi, ILogger<IntegrationsController> logger)
    {
        _graphApi = graphApi;
        _logger = logger;
    }

    /// <summary>
    /// Trigger an immediate AD sync.
    ///
    /// This used to call a Graph read and return its count, writing nothing — so it
    /// reported "synced: 3" while the directory stayed exactly as it was. It now runs the
    /// same sync the timer does, and reports what was actually written, including any
    /// users that could not be stored and why.
    /// </summary>
    [HttpPost("sync/ad")]
    [Authorize(Policy = "RequireAdminFull")]
    public async Task<ActionResult> SyncActiveDirectory(
        [FromServices] IAdDirectorySyncService sync, CancellationToken ct)
    {
        // Full sync rather than delta: someone pressing this wants the directory
        // reconciled now, not the increment since the last scheduled run.
        var result = await sync.SyncAsync(null, ct);
        return Ok(new
        {
            fetched = result.Fetched,
            created = result.Created,
            updated = result.Updated,
            deactivated = result.Deactivated,
            skipped = result.Skipped,
        });
    }

    /// <summary>Send a test Teams notification to a user.</summary>
    [HttpPost("notify/teams")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult> SendTeamsNotification([FromBody] TeamsNotificationRequest request)
    {
        await _graphApi.SendTeamsNotificationAsync(request.UserId, request.Title, request.Message);
        _logger.LogInformation("Teams notification sent to {UserId}: {Title}", request.UserId, request.Title);
        return Ok(new { sent = true });
    }

    /// <summary>Push an on-call shift to Outlook calendar.</summary>
    [HttpPost("calendar/push")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult> PushToCalendar([FromBody] CalendarPushRequest request)
    {
        await _graphApi.CreateOutlookCalendarEventAsync(
            request.UserId, request.Subject, request.StartTime, request.EndTime);
        return Ok(new { pushed = true });
    }

    /// <summary>Get presence for a user from Teams.</summary>
    [HttpGet("presence/{userId}")]
    public async Task<ActionResult> GetPresence(string userId)
    {
        var presence = await _graphApi.GetUserPresenceAsync(userId);
        return Ok(new { userId, presence });
    }
}

public record TeamsNotificationRequest(string UserId, string Title, string Message);
public record CalendarPushRequest(string UserId, string Subject, DateTime StartTime, DateTime EndTime);
