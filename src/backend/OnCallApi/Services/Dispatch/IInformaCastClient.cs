namespace OnCallApi.Services.Dispatch;

/// <summary>
/// Client for Singlewire InformaCast Fusion REST API.
/// Triggers emergency scenarios, sends alerts, and checks broadcast status.
/// </summary>
public interface IInformaCastClient
{
    /// <summary>Verify API connectivity and token validity.</summary>
    Task<ConnectionStatus> CheckConnectionAsync();

    /// <summary>Trigger an emergency scenario by ID.</summary>
    Task<InformaCastResult> TriggerScenarioAsync(string scenarioId, string location, string message);

    /// <summary>Send a direct notification to a recipient group.</summary>
    Task<InformaCastResult> SendAlertAsync(string recipientGroup, string message, string priority = "CRITICAL");

    /// <summary>Check the status of a previously triggered scenario.</summary>
    Task<string> GetScenarioStatusAsync(string incidentId);
}
