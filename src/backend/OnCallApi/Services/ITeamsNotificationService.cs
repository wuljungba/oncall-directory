namespace OnCallApi.Services;

/// <summary>
/// Sends Microsoft Teams notifications.
///
/// Every method reports whether the message was actually delivered. That is the point of
/// the interface: escalation is a safety-critical path, and callers previously had no way
/// to distinguish an alert that reached a clinician from one that was swallowed.
/// </summary>
public interface ITeamsNotificationService
{
    Task<bool> SendNotificationAsync(
        string userAzureAdId, string title, string message,
        NotificationCardType cardType = NotificationCardType.Info);

    Task<bool> SendShiftStartingAsync(
        string userId, string userName, string tier, DateTime startTime, string department);

    Task<bool> SendSwapRequestedAsync(
        string requesterId, string requesterName, string targetId, string targetName, string shiftInfo);

    Task<bool> SendSwapApprovedAsync(string approverId, string requesterName, string shiftInfo);

    Task<bool> SendGapAlertAsync(string userId, string department, DateTime gapDate);

    Task<bool> SendEscalationAsync(string userId, string department, string tier, string details);
}
