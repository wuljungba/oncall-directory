namespace OnCallApi.Authorization;

/// <summary>
/// Which application settings must not be handed back in full.
///
/// AppSettings is a public-ish key-value bag: GET /api/settings returns every row to anyone
/// holding Schedule.Read, which is most of the directory. That was fine while it held rotation
/// defaults and dial-plan prefixes, and stopped being fine when the Graph delta link was
/// written into it — a resource URL that resumes another organisation's directory enumeration.
///
/// The delta link has since moved to its own table. This stays as the rule for whatever gets
/// written here next, because the next secret in a key-value bag is only a matter of time.
/// </summary>
public static class AppSettingSensitivity
{
    /// <summary>
    /// Substrings that mark a value as not-for-display. Matched case-insensitively against the
    /// whole key, so a namespaced key ("integrations.twilio.authToken") is caught too.
    /// </summary>
    private static readonly string[] SensitiveMarkers =
    [
        "token", "secret", "password", "apikey", "api_key", "credential", "connectionstring",
    ];

    public static bool IsSensitive(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        return SensitiveMarkers.Any(marker => key.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
