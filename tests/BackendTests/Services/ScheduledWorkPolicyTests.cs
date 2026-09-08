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

    [Fact]
    public void ShouldRun_ExplicitTrue_BeatsANonProductionSlot()
    {
        ScheduledWorkPolicy.ShouldRun(configured: true, slotName: "staging").Should().BeTrue();
    }

    [Fact]
    public void ShouldRun_ExplicitFalse_BeatsTheLiveSlot()
    {
        // The override cuts both ways on purpose, which is exactly why it must never be set
        // as an Azure app setting: those are non-sticky and swap between slots on deploy, so
        // a "false" pinned to staging would follow the swap into production.
        ScheduledWorkPolicy.ShouldRun(configured: false, slotName: "Production").Should().BeFalse();
    }
}
