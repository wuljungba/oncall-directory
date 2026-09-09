using FluentAssertions;
using OnCallApi.Configuration;

namespace BackendTests.Services;

/// <summary>
/// Guards the slot-gating invariant: the timer-driven background services run on the live
/// slot and nowhere else. Both slots run alwaysOn against the same database, and the
/// escalation engine has no lease, so a second copy pages the same clinician twice for one
/// unacknowledged shift.
///
/// Two failure directions matter equally. Returning true on staging is the duplicate page.
/// Returning false where there is no slot at all would silently stop every sync in local
/// development — and, if it ever happened on the live slot, would stop escalation entirely,
/// which looks exactly like a quiet night.
/// </summary>
public class ScheduledWorkPolicyTests
{
    [Theory]
    [InlineData(null)]        // not on App Service at all -- local development
    [InlineData("")]          // variable present but empty
    [InlineData("   ")]       // whitespace is not a slot name
    public void ShouldRun_WithoutASlotName_RunsSoLocalDevelopmentIsUnchanged(string? slot)
    {
        ScheduledWorkPolicy.ShouldRun(configured: null, slotName: slot).Should().BeTrue();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData("PRODUCTION")]
    public void ShouldRun_OnTheLiveSlot_Runs(string slot)
    {
        ScheduledWorkPolicy.ShouldRun(configured: null, slotName: slot).Should().BeTrue();
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("Staging")]
    [InlineData("STAGING")]
    [InlineData("preview")]   // any non-production slot, not just the one we happen to have
    public void ShouldRun_OnAnyOtherSlot_DoesNotRun(string slot)
    {
        ScheduledWorkPolicy.ShouldRun(configured: null, slotName: slot).Should().BeFalse();
    }

    // ── The case this was actually deployed into ──
    //
    // Linux App Service does not put WEBSITE_SLOT_NAME in the app container, so the staging
    // slot looks identical to a developer laptop: no slot name at all. Relying on the slot
    // name alone meant the gate read "local development" and started all seven services on
    // staging, against production's database. The setting is what closes that.

    [Fact]
    public void ShouldRun_SettingFalse_DecidesEvenWhenNoSlotNameIsReported()
    {
        ScheduledWorkPolicy.ShouldRun(configured: false, slotName: null).Should().BeFalse();
    }

    [Fact]
    public void ShouldRun_SettingTrue_DecidesEvenWhenNoSlotNameIsReported()
    {
        ScheduledWorkPolicy.ShouldRun(configured: true, slotName: null).Should().BeTrue();
    }

    [Fact]
    public void ShouldRun_ExplicitTrue_BeatsANonProductionSlot()
    {
        ScheduledWorkPolicy.ShouldRun(configured: true, slotName: "staging").Should().BeTrue();
    }

    [Fact]
    public void ShouldRun_ExplicitFalse_BeatsTheLiveSlot()
    {
        // The setting cuts both ways on purpose -- in Azure it is what actually decides this,
        // because WEBSITE_SLOT_NAME is not present in the app container on Linux App Service.
        // That is also why it must be sticky: a "false" that swapped out of staging would stop
        // every sync and the escalation engine in production.
        ScheduledWorkPolicy.ShouldRun(configured: false, slotName: "Production").Should().BeFalse();
    }
}
