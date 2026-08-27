using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;
using OnCallApi.Services.Dispatch;

namespace BackendTests.Services;

/// <summary>
/// Guards the dispatch pipeline against a disabled channel's client taking the whole code
/// call down.
///
/// CiscoCucmClient, InformaCastClient and VoceraClient all build their base URI in the
/// constructor, and all three throw UriFormatException when their host/base URL is blank —
/// which is exactly the state of a channel that is switched off. The pipeline used to
/// resolve all four clients up front, so on the DEFAULT configuration a code call threw
/// before attempting a single channel: one opaque "Invalid URI" step, nobody notified, and
/// the event left unacknowledged.
///
/// The rest of the dispatch suite registers hand-written stubs, which construct happily and
/// therefore cannot catch this. These tests deliberately register the REAL clients.
/// </summary>
public class CodeCallDispatchClientResolutionTests
{
    private static ServiceProvider BuildProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddHttpClient();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));

        // The real implementations, exactly as Program.cs registers them.
        services.AddScoped<ICiscoCucmClient, CiscoCucmClient>();
        services.AddScoped<IInformaCastClient, InformaCastClient>();
        services.AddScoped<IVoceraClient, VoceraClient>();
        services.AddScoped<ITwilioClient, TwilioClient>();

        // Dispatch runs outside any request, so it sees the whole estate — the same
        // posture TenantScope adopts when there is no HttpContext.
        services.AddScoped<IPhoneTreeEventService>(sp => new PhoneTreeEventService(
            sp.GetRequiredService<AppDbContext>(),
            TestTenantScopes.Unrestricted,
            NullLogger<PhoneTreeEventService>.Instance));

        return services.BuildServiceProvider();
    }

    private static int SeedEvent(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tree = new PhoneTree { Id = 1, Name = "Code Blue", TreeType = "code-blue" };
        var evt = new PhoneTreeEvent
        {
            Id = 1,
            PhoneTreeId = tree.Id,
            StartedAt = DateTime.UtcNow,
            Location = "ICU 4",
        };

        db.PhoneTrees.Add(tree);
        db.PhoneTreeEvents.Add(evt);
        db.SaveChanges();
        return evt.Id;
    }

    private static CodeCallDispatchService BuildService(
        IServiceProvider provider, DispatchOptions options) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<OnCallApi.Hubs.OnCallNotificationHub>>(),
            Options.Create(options),
            new DispatchJobQueue(),
            NullLogger<CodeCallDispatchService>.Instance);

    private static async Task<List<DispatchStep>> StepsAsync(IServiceProvider provider, int eventId)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DispatchSteps
            .Where(s => s.PhoneTreeEventId == eventId)
            .OrderBy(s => s.Id)
            .ToListAsync();
    }

    /// <summary>
    /// The default configuration: every channel off, so every client would throw on
    /// construction. The pipeline must still run to completion.
    /// </summary>
    [Fact]
    public async Task AllChannelsDisabled_RunsFullPipeline_WithoutResolvingAnyClient()
    {
        var provider = BuildProvider(Guid.NewGuid().ToString());
        var eventId = SeedEvent(provider);

        await BuildService(provider, new DispatchOptions()).ProcessDispatchJobAsync(eventId, "code-blue");

        var steps = await StepsAsync(provider, eventId);

        steps.Should().NotContain(s => s.StepKey == "pipeline_error",
            "an unconfigured channel must never abort the dispatch pipeline");
        steps.Should().Contain(s => s.StepKey == "cucm_axl_check" && s.Status == "skipped");
        steps.Should().Contain(s => s.StepKey == "informacast" && s.Status == "skipped");
        steps.Should().Contain(s => s.StepKey == "vocera" && s.Status == "skipped");
        steps.Should().Contain(s => s.StepKey == "twilio_sms" && s.Status == "skipped");
    }

    /// <summary>
    /// A code call that reached nobody must not report success.
    ///
    /// With no channel configured the pipeline used to record
    /// "acknowledged / completed — operating in stub mode" and machine-acknowledge the
    /// event, so an operator saw a dispatched code blue when not one person had been
    /// contacted. That is production's exact configuration.
    /// </summary>
    [Fact]
    public async Task NoChannelsConfigured_FailsTheDispatch_AndDoesNotAcknowledge()
    {
        var provider = BuildProvider(Guid.NewGuid().ToString());
        var eventId = SeedEvent(provider);

        await BuildService(provider, new DispatchOptions()).ProcessDispatchJobAsync(eventId, "code-blue");

        var steps = await StepsAsync(provider, eventId);
        var outcome = steps.Single(s => s.StepKey == "acknowledged");

        outcome.Status.Should().Be("failed",
            "nobody was contacted, so the dispatch did not succeed");
        outcome.Detail.Should().Contain("nobody was contacted");

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evt = await db.PhoneTreeEvents.FindAsync(eventId);

        evt!.AcknowledgedAt.Should().BeNull(
            "an unacknowledged event keeps demanding attention in the Command Center");
    }

    /// <summary>
    /// The realistic hospital configuration for this deployment: SMS only. CUCM,
    /// InformaCast and Vocera stay unconfigured and must not prevent the SMS attempt.
    /// </summary>
    [Fact]
    public async Task OnlyTwilioEnabled_StillReachesTheSmsStep_WithOtherClientsUnconfigured()
    {
        var provider = BuildProvider(Guid.NewGuid().ToString());
        var eventId = SeedEvent(provider);

        var options = new DispatchOptions
        {
            Twilio = new TwilioOptions
            {
                Enabled = true,
                AccountSid = "ACtest",
                AuthToken = "token",
                FromNumber = "+12025550100",
            },
        };

        await BuildService(provider, options).ProcessDispatchJobAsync(eventId, "code-blue");

        var steps = await StepsAsync(provider, eventId);

        steps.Should().NotContain(s => s.StepKey == "pipeline_error");

        // No shift is seeded, so there is no on-call provider. That is a dispatch FAILURE,
        // not a skip — the alert reached nobody and the event must not be acknowledged.
        steps.Should().Contain(s => s.StepKey == "twilio_sms" && s.Status == "failed");
        steps.Should().Contain(s => s.StepKey == "sip_fallback" && s.Status == "failed");
    }
}
