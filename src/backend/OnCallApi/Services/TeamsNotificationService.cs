using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Service for sending Microsoft Teams notifications via Graph API.
/// Builds adaptive card payloads for different on-call event types.
/// </summary>
public class TeamsNotificationService : ITeamsNotificationService
{
    private readonly IGraphApiService _graphApi;
    private readonly ILogger<TeamsNotificationService> _logger;

    public TeamsNotificationService(IGraphApiService graphApi, ILogger<TeamsNotificationService> logger)
    {
        _graphApi = graphApi;
        _logger = logger;
    }

    /// <summary>
    /// Send a notification to a user's Teams chat. Returns false if it was not delivered.
    ///
    /// This used to return void and swallow every failure, so callers — including the
    /// escalation path — could not tell a delivered alert from one that vanished.
    /// </summary>
    public async Task<bool> SendNotificationAsync(string userAzureAdId, string title, string message, NotificationCardType cardType = NotificationCardType.Info)
    {
        try
        {
            var cardJson = BuildAdaptiveCard(title, message, cardType);
            var delivered = await _graphApi.SendTeamsMessageAsync(userAzureAdId, cardJson);

            if (delivered)
            {
                _logger.LogInformation("Teams notification sent to {UserId}: {Title}", userAzureAdId, title);
            }
            else
            {
                _logger.LogWarning("Teams notification NOT delivered to {UserId}: {Title}", userAzureAdId, title);
            }

            return delivered;
        }
        catch (ODataError ex)
        {
            _logger.LogWarning(ex, "Failed to send Teams notification to {UserId} (may need app installed in Teams)", userAzureAdId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending Teams notification to {UserId}", userAzureAdId);
            return false;
        }
    }

    /// <summary>Notify a user their shift is starting soon.</summary>
    public Task<bool> SendShiftStartingAsync(string userId, string userName, string tier, DateTime startTime, string department)
    {
        var startLocal = startTime.ToLocalTime().ToString("h:mm tt");
        var dateLocal = startTime.ToLocalTime().ToString("dddd, MMM d");
        return SendNotificationAsync(userId,
            "🔔 Shift Starting Soon",
            $"**{userName}** — your **{tier}** on-call shift at *{department}* starts **{dateLocal} at {startLocal}**.",
            NotificationCardType.Warning);
    }

    /// <summary>Notify relevant parties about a swap request.</summary>
    public Task<bool> SendSwapRequestedAsync(string requesterId, string requesterName, string targetId, string targetName, string shiftInfo)
    {
        return SendNotificationAsync(targetId,
            "🔄 Swap Request",
            $"**{requesterName}** has requested to swap shifts with you.\n\nShift: {shiftInfo}",
            NotificationCardType.Info);
    }

    /// <summary>Notify about an approved swap.</summary>
    public Task<bool> SendSwapApprovedAsync(string approverId, string requesterName, string shiftInfo)
    {
        return SendNotificationAsync(approverId,
            "✅ Swap Approved",
            $"Your swap request has been approved.\n\nShift: {shiftInfo}",
            NotificationCardType.Success);
    }

    /// <summary>Alert about a coverage gap.</summary>
    public Task<bool> SendGapAlertAsync(string userId, string department, DateTime gapDate)
    {
        var dateStr = gapDate.ToLocalTime().ToString("MMM d, yyyy");
        return SendNotificationAsync(userId,
            "⚠️ Coverage Gap Detected",
            $"The **{department}** schedule has an uncovered shift on **{dateStr}**.\n\nPlease arrange coverage.",
            NotificationCardType.Error);
    }

    /// <summary>Escalation notice.</summary>
    public Task<bool> SendEscalationAsync(string userId, string department, string tier, string details)
    {
        return SendNotificationAsync(userId,
            "🚨 Escalation Alert",
            $"**{department}** — {tier} escalation.\n\n{details}",
            NotificationCardType.Error);
    }

    private static string BuildAdaptiveCard(string title, string message, NotificationCardType cardType)
    {
        var accentColor = cardType switch
        {
            NotificationCardType.Success => "Good",
            NotificationCardType.Warning => "Warning",
            NotificationCardType.Error => "Attention",
            _ => "Accent"
        };

        // Simple JSON payload. For production, use the full Adaptive Card schema.
        return $$"""
        {
            "subject": "{{EscapeJson(title)}}",
            "body": {
                "contentType": "html",
                "content": "{{EscapeJson(message)}}"
            }
        }
        """;
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "<br>").Replace("\r", "");
    }
}

public enum NotificationCardType
{
    Info,
    Success,
    Warning,
    Error
}
