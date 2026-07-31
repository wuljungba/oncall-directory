namespace OnCallApi.Services.Dispatch;

/// <summary>Result of a connection health check.</summary>
public class ConnectionStatus
{
    public bool Connected { get; set; }
    public string Detail { get; set; } = string.Empty;
    public DateTime LastCheckedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Result of a dispatch attempt.</summary>
public class DispatchResult
{
    public bool Success { get; set; }
    public string IncidentId { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

/// <summary>Priority level for Vocera alerts.</summary>
public enum VoceraPriority
{
    Low = 1,
    Normal = 3,
    High = 5,
    Critical = 7,
    Emergency = 10,
}

public class CucmPageResult
{
    public bool Success { get; set; }
    public string PageId { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public class InformaCastResult
{
    public bool Success { get; set; }
    public string IncidentId { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public class VoceraMessageResult
{
    public bool Success { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}
