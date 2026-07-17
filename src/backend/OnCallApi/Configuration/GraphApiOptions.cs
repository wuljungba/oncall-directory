namespace OnCallApi.Configuration;

public class GraphApiOptions
{
    public const string SectionName = "GraphApi";

    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}
