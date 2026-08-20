namespace OnCallApi.Configuration;

/// <summary>Configuration for emergency dispatch integrations.</summary>
public class DispatchOptions
{
    public const string SectionName = "Dispatch";

    public CucmOptions Cucm { get; set; } = new();
    public InformaCastOptions InformaCast { get; set; } = new();
    public VoceraOptions Vocera { get; set; } = new();
    public TwilioOptions Twilio { get; set; } = new();
    public SipPbxOptions SipPbx { get; set; } = new();
}

public class CucmOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 8443;
    public string AxlUsername { get; set; } = string.Empty;
    public string AxlPassword { get; set; } = string.Empty;
    public string AxlWsdlUrl { get; set; } = string.Empty;
    public string PageExtension { get; set; } = string.Empty;
    public string PageGroupNumber { get; set; } = string.Empty;
    public bool UseHttps { get; set; } = true;
    public int ConnectionTimeoutSeconds { get; set; } = 30;
}

public class InformaCastOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string ScenarioId { get; set; } = string.Empty;
    public int ConnectionTimeoutSeconds { get; set; } = 30;
}

public class VoceraOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ResponderGroupId { get; set; } = string.Empty;
    public int ConnectionTimeoutSeconds { get; set; } = 30;
}

public class TwilioOptions
{
    public bool Enabled { get; set; }
    public string AccountSid { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;
    public string FromNumber { get; set; } = string.Empty;

    /// <summary>
    /// Optional Messaging Service SID. When set it is used instead of <see cref="FromNumber"/>.
    /// Preferred for US A2P 10DLC traffic, where the campaign is attached to the messaging
    /// service rather than to an individual long code.
    /// </summary>
    public string MessagingServiceSid { get; set; } = string.Empty;

    /// <summary>
    /// Absolute, publicly reachable URL Twilio posts delivery status updates to
    /// (e.g. https://app-oncall-production.azurewebsites.net/api/public/twilio/status).
    /// Blank means no status callback is requested — in which case an undelivered
    /// code-call SMS is never surfaced, so this should always be set in production.
    /// </summary>
    public string StatusCallbackUrl { get; set; } = string.Empty;

    public int ConnectionTimeoutSeconds { get; set; } = 30;

    /// <summary>The sender to use: the messaging service when configured, else the from number.</summary>
    public bool UseMessagingService => !string.IsNullOrWhiteSpace(MessagingServiceSid);
}

public class SipPbxOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public string PagingExtension { get; set; } = string.Empty;
}
