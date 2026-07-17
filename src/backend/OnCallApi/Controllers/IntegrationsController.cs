using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/integrations")]
[Authorize(Policy = "RequireViewer")]
public class IntegrationsController : ControllerBase
{
    private readonly IGraphApiService _graphApi;
    private readonly ILogger<IntegrationsController> _logger;

    public IntegrationsController(IGraphApiService graphApi, ILogger<IntegrationsController> logger)
    {
        _graphApi = graphApi;
        _logger = logger;
    }

    /// <summary>Trigger an immediate AD sync.</summary>
    [HttpPost("sync/ad")]
    [Authorize(Policy = "RequireAdmin")]
    public async Task<ActionResult> SyncActiveDirectory()
    {
        var users = await _graphApi.SyncUsersAsync();
        return Ok(new { synced = users.Count });
    }

    /// <summary>Send a test Teams notification to a user.</summary>
    [HttpPost("notify/teams")]
    [Authorize(Policy = "RequireScheduler")]
    public async Task<ActionResult> SendTeamsNotification([FromBody] TeamsNotificationRequest request)
    {
        await _graphApi.SendTeamsNotificationAsync(request.UserId, request.Title, request.Message);
        _logger.LogInformation("Teams notification sent to {UserId}: {Title}", request.UserId, request.Title);
        return Ok(new { sent = true });
    }

    /// <summary>Push an on-call shift to Outlook calendar.</summary>
    [HttpPost("calendar/push")]
    [Authorize(Policy = "RequireScheduler")]
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
