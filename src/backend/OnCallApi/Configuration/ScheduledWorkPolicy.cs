namespace OnCallApi.Configuration;

/// <summary>
/// Decides whether this instance runs the timer-driven background services.
///
/// Both deployment slots run with alwaysOn and point at the same database, so before this
/// existed the staging slot ran AD sync, department sync, presence, calendar push and the
/// two retention sweeps against live production data. The escalation engine is why it
/// mattered most: <c>CheckAndEscalateAsync</c> reads the last EscalationEvent for a shift and
/// then decides whether to page, with nothing atomic in between, so two instances on
/// unsynchronised two-minute timers page the same clinician twice for the same
/// unacknowledged shift.
///
/// The answer comes from the app setting named by <see cref="ConfigKey"/>: true on the live
/// slot, false on staging. That setting MUST be listed in the site's slotConfigNames so it is
/// sticky and stays with its slot — main.bicep declares it. An ordinary, non-sticky setting
/// would travel with a swap, and staging's "false" landing on production would silently stop
/// every sync and the escalation engine, which looks exactly like a quiet night.
///
/// WEBSITE_SLOT_NAME is only a fallback, and a weak one: on the Linux App Service this runs
/// on, the platform does not put it in the app container's environment, so it reads as absent
/// on staging just as it does on a laptop. It is still consulted because it costs nothing and
/// is correct where it is present, but the app setting is what actually decides this in Azure.
/// An absent slot name therefore means "assume local development and run everything" — the
/// safe default for a developer, and the reason the setting is not optional in Azure.
///
/// This governs the staging slot only. It is NOT protection against two instances of the same
/// slot -- that needs a lease on the escalation path, and scaling out without one reintroduces
/// the duplicate page this prevents.
/// </summary>
public static class ScheduledWorkPolicy
{
    /// <summary>
    /// The setting that decides this. Set per slot and marked sticky in main.bicep; also
    /// usable locally or in tests to force either behaviour.
    /// </summary>
    public const string ConfigKey = "BackgroundServices:RunScheduledWork";

    /// <summary>The name App Service gives the live slot, where the variable is present.</summary>
    private const string ProductionSlot = "Production";

    /// <summary>The platform variable naming the slot -- absent on Linux App Service.</summary>
    private const string SlotNameVariable = "WEBSITE_SLOT_NAME";

    /// <summary>
    /// The decision, as a pure function so it can be tested without an App Service.
    /// The explicit setting wins, and in Azure it is always the one that answers. Only when
    /// nothing is configured does the slot name get a say, and an absent one means we are
    /// probably not on App Service at all.
    /// </summary>
    public static bool ShouldRun(bool? configured, string? slotName)
    {
        if (configured.HasValue) return configured.Value;

        return string.IsNullOrWhiteSpace(slotName)
            || slotName.Equals(ProductionSlot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the setting from configuration and the slot from the platform.</summary>
    public static bool ShouldRun(IConfiguration config) =>
        ShouldRun(config.GetValue<bool?>(ConfigKey), CurrentSlotName());

    /// <summary>
    /// The slot this instance reports running in, or null -- which is the normal answer both
    /// locally and on Linux App Service. Exposed so diagnostics can show what the decision was
    /// made from; after a swap that is the one thing worth checking.
    /// </summary>
    public static string? CurrentSlotName() =>
        Environment.GetEnvironmentVariable(SlotNameVariable);
}
