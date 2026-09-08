namespace OnCallApi.Configuration;

/// <summary>
/// Decides whether this instance runs the timer-driven background services.
///
/// Both deployment slots run with alwaysOn and point at the same database, so
/// before this existed the staging slot ran AD sync, department sync, presence,
/// calendar push and the two retention sweeps against live production data. The
/// escalation engine is why it mattered most: <c>CheckAndEscalateAsync</c> reads the
/// last EscalationEvent for a shift and then decides whether to page, with nothing
/// atomic in between, so two instances on unsynchronised two-minute timers page the
/// same clinician twice for the same unacknowledged shift.
///
/// The decision reads WEBSITE_SLOT_NAME, which App Service injects per slot --
/// "Production" on the live slot, the slot's own name elsewhere. That it is a
/// platform variable rather than an app setting is the whole point: app settings are
/// non-sticky and swap between slots on every deploy, so an explicit "false" pinned
/// to staging would follow the swap into production and silently disable every
/// background service there. A platform variable cannot swap.
///
/// This governs the staging slot only. It is NOT protection against two instances of
/// the same slot -- that needs a lease on the escalation path, and scaling out
/// without one reintroduces the duplicate page this prevents.
/// </summary>
public static class ScheduledWorkPolicy
{
    /// <summary>
    /// Escape hatch for local runs and tests. Deliberately absent from every
    /// appsettings file and from the Bicep template: setting it in Azure app settings
    /// would reintroduce the swap hazard described above.
    /// </summary>
    public const string ConfigKey = "BackgroundServices:RunScheduledWork";

    /// <summary>The name App Service gives the live slot.</summary>
    private const string ProductionSlot = "Production";

    /// <summary>The platform variable naming the slot this instance runs in.</summary>
    private const string SlotNameVariable = "WEBSITE_SLOT_NAME";

    /// <summary>
    /// The decision, as a pure function so it can be tested without an App Service.
    /// An explicit setting wins. Otherwise an absent slot name means we are not on App
    /// Service at all -- local development -- and the services run as they always have.
    /// </summary>
    public static bool ShouldRun(bool? configured, string? slotName)
    {
        if (configured.HasValue) return configured.Value;

        return string.IsNullOrWhiteSpace(slotName)
            || slotName.Equals(ProductionSlot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the override from configuration and the slot from the platform.</summary>
    public static bool ShouldRun(IConfiguration config) =>
        ShouldRun(config.GetValue<bool?>(ConfigKey), CurrentSlotName());

    /// <summary>
    /// The slot this instance runs in, or null off App Service. Exposed so diagnostics
    /// can report what the decision was actually made from -- after a swap that is the
    /// one thing worth checking.
    /// </summary>
    public static string? CurrentSlotName() =>
        Environment.GetEnvironmentVariable(SlotNameVariable);
}
