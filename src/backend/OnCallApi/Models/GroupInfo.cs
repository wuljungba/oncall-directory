namespace OnCallApi.Models;

/// <summary>
/// Lightweight DTO for Microsoft 365 Group information.
/// </summary>
public record GroupInfo(string Id, string Name, string? Description);
