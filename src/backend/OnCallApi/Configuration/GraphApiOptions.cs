namespace OnCallApi.Configuration;

public class GraphApiOptions
{
    public const string SectionName = "GraphApi";

    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Microsoft Graph API permission scopes.
    /// Defaults to app-only ".default" scope for backward compatibility.
    /// Can be overridden in configuration (e.g., GraphApi:Scopes__0) for granular permissions.
    /// </summary>
    public string[] Scopes { get; set; } = ["https://graph.microsoft.com/.default"];
}
