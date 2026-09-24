namespace OnCallApi.Configuration;

/// <summary>
/// How long records must be kept, and the check that the configured value says so.
///
/// <c>Hipaa:AuditLogRetentionDays</c> sat in appsettings, in the Bicep template and in three
/// documents for months while being read by <b>no code at all</b> — a number that described an
/// intention nothing enforced. Retention that is only written down is not retention; the first
/// anyone would learn of a shortfall is when a record was asked for and did not exist.
///
/// So the value is validated at startup and the services that could shorten a record's life
/// consult it. Configuration that nothing reads is indistinguishable from configuration that
/// is wrong.
/// </summary>
public static class RetentionPolicy
{
    /// <summary>
    /// The floor, in days: seven years.
    ///
    /// HIPAA §164.316(b)(2)(i) requires six years for documentation. Seven is the operating
    /// policy for this deployment — it covers the six-year federal floor plus the longer
    /// retention several states impose on incident records, and it is what the code-call
    /// history is kept for.
    /// </summary>
    public const int MinimumDays = 2555;

    public const string ConfigKey = "Hipaa:AuditLogRetentionDays";

    /// <summary>The configured retention, or the floor when nothing is configured.</summary>
    public static int ConfiguredDays(IConfiguration config) =>
        config.GetValue(ConfigKey, MinimumDays);

    /// <summary>
    /// Checks the configured retention against the floor.
    ///
    /// Fatal outside Development, matching how the app already treats secret placeholders and
    /// <c>DevAuth</c>: a production instance that would silently under-retain should refuse to
    /// start rather than discover the shortfall years later. In Development it is a warning,
    /// because a laptop is not keeping anybody's records.
    /// </summary>
    public static void Validate(IConfiguration config, IHostEnvironment env, ILogger logger)
    {
        var configured = ConfiguredDays(config);
        if (configured >= MinimumDays)
        {
            logger.LogInformation(
                "Record retention is {Days} days ({Years:F1} years)", configured, configured / 365.0);
            return;
        }

        var message =
            $"{ConfigKey} is {configured} days, below the {MinimumDays}-day ({MinimumDays / 365.0:F1}-year) "
            + "retention this deployment is required to keep. Raise it, or records will be "
            + "retained for less time than the policy claims.";

        if (env.IsDevelopment())
        {
            logger.LogWarning("{Message}", message);
            return;
        }

        throw new InvalidOperationException(message);
    }
}
