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
/// The SIP "fallback" ran only after every real channel had failed, and then reported a
/// page it had never sent.
///
/// It was `await Task.Delay(500); // Simulate SIP call setup`, followed by a completed
/// step and successCount++. That increment is what made it dangerous rather than merely
/// useless: both nobody-was-contacted alarms are guarded on successCount == 0, so the
/// simulation suppressed them, the Command Center stopped demanding attention, and the
/// operator was told their code blue had gone out.
///
/// Unlike Twilio, InformaCast, Vocera and CUCM, SIP has no client anywhere in this
/// codebase. The delay WAS the implementation.
/// </summary>
public class CodeCallSipFallbackTests
{
    private sealed class FailingTwilioClient : ITwilioClient
    {
        public Task<ConnectionStatus> CheckConnectionAsync() =>
            Task.FromResult(new ConnectionStatus { Connected = true, Detail = "stub" });

        public Task<DispatchResult> SendSmsAsync(string toPhone, string message) =>
            Task.FromResult(new DispatchResult { Success = false, Detail = "carrier rejected" });
    }

    private sealed class StubCucmClient : ICiscoCucmClient
    {
        public Task<ConnectionStatus> CheckConnectionAsync() => Task.FromResult(new ConnectionStatus());
        public Task<bool> CheckDeviceRegistrationAsync(string location) => Task.FromResult(true);
        public Task<CucmPageResult> InitiatePageAsync(string callingParty, string dialedNumber, string location) =>
            Task.FromResult(new CucmPageResult());
        public Task<int> GetRegisteredDeviceCountAsync() => Task.FromResult(0);
    }

    private sealed class StubInformaCastClient : IInformaCastClient
    {
        public Task<ConnectionStatus> CheckConnectionAsync() => Task.FromResult(new ConnectionStatus());
        public Task<InformaCastResult> TriggerScenarioAsync(string scenarioId, string location, string message) =>
            Task.FromResult(new InformaCastResult());
        public Task<InformaCastResult> SendAlertAsync(string recipientGroup, string message, string priority = "CRITICAL") =>
            Task.FromResult(new InformaCastResult());
        public Task<string> GetScenarioStatusAsync(string incidentId) => Task.FromResult("unknown");
    }

    private sealed class StubVoceraClient : IVoceraClient
    {
        public Task<ConnectionStatus> CheckConnectionAsync() => Task.FromResult(new ConnectionStatus());
        public Task<VoceraMessageResult> SendAlertAsync(
            string badgeId, string message, VoceraPriority priority = VoceraPriority.Critical) =>
            Task.FromResult(new VoceraMessageResult());
        public Task<bool> CancelAlertAsync(string eventId) => Task.FromResult(true);
        public Task<bool> GetDeviceStatusAsync(string badgeId) => Task.FromResult(true);
    }

    /// <summary>
    /// A pipeline whose only real channel is a Twilio client that always fails, so every
    /// run lands in the fallback branch.
    /// </summary>
    private static (CodeCallDispatchService Service, int EventId, IServiceProvider Provider)
        BuildFailingPipeline(bool sipEnabled)
    {
        // The name is captured ONCE. Calling Guid.NewGuid() inside the lambda gives every
        // scope its own database, so the seed, the dispatch and the assertions each talk to
        // a different one and nothing appears to happen at all.
        var dbName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<ICiscoCucmClient>(_ => new StubCucmClient());
        services.AddScoped<IInformaCastClient>(_ => new StubInformaCastClient());
        services.AddScoped<IVoceraClient>(_ => new StubVoceraClient());
        services.AddScoped<ITwilioClient>(_ => new FailingTwilioClient());
        services.AddScoped<IPhoneTreeEventService>(sp => new PhoneTreeEventService(
            sp.GetRequiredService<AppDbContext>(),
            TestTenantScopes.Unrestricted,
            NullLogger<PhoneTreeEventService>.Instance));

        var provider = services.BuildServiceProvider();

        int eventId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var dept = new Department { Id = 1, Name = "Cardiology" };
            var tree = new PhoneTree { Id = 1, Name = "Code Blue", TreeType = "code-blue", DepartmentId = dept.Id };
            var employee = new Employee
            {
                Id = Guid.NewGuid(),
                FirstName = "Test",
                LastName = "Provider",
                Email = "provider@example.test",
                MobilePhone = "+12025550134",
            };
            var schedule = new Schedule { Id = 1, Name = "Cardiology call", DepartmentId = dept.Id };
            var now = DateTime.UtcNow;

            db.Departments.Add(dept);
            db.PhoneTrees.Add(tree);
            db.Employees.Add(employee);
            db.Schedules.Add(schedule);
            db.Shifts.Add(new Shift
            {
                Id = 1,
                ScheduleId = schedule.Id,
                EmployeeId = employee.Id,
                Tier = "primary",
                Status = "scheduled",
                StartTime = now.AddHours(-1),
                EndTime = now.AddHours(1),
            });

            var evt = new PhoneTreeEvent { Id = 1, PhoneTreeId = tree.Id, StartedAt = now, Location = "ICU 4" };
            db.PhoneTreeEvents.Add(evt);
            db.SaveChanges();
            eventId = evt.Id;
        }

        var options = Options.Create(new DispatchOptions
        {
            Twilio = new TwilioOptions
            {
                Enabled = true,
                AccountSid = "ACtest",
                AuthToken = "token",
                FromNumber = "+12025550100",
            },
            SipPbx = new SipPbxOptions
            {
                Enabled = sipEnabled,
                Host = "pbx.hospital.example",
                PagingExtension = "5000",
            },
        });

        var service = new CodeCallDispatchService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<OnCallApi.Hubs.OnCallNotificationHub>>(),
            options,
            new DispatchJobQueue(),
            NullLogger<CodeCallDispatchService>.Instance);

        return (service, eventId, provider);
    }

    private static async Task<DispatchStep?> StepAsync(IServiceProvider provider, int eventId, string key)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DispatchSteps
            .Where(s => s.PhoneTreeEventId == eventId && s.StepKey == key)
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync();
    }

    private static async Task<PhoneTreeEvent> EventAsync(IServiceProvider provider, int eventId)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PhoneTreeEvents.AsNoTracking().FirstAsync(e => e.Id == eventId);
    }

    /// <summary>
    /// The regression itself. With SIP switched on and every real channel failing, the
    /// pipeline used to record a completed page and acknowledge the event.
    /// </summary>
    [Fact]
    public async Task SipFallbackNeverReportsAPageItDidNotSend()
    {
        var (service, eventId, provider) = BuildFailingPipeline(sipEnabled: true);

        await service.ProcessDispatchJobAsync(eventId, "code-blue");

        var sip = await StepAsync(provider, eventId, "sip_fallback");
        sip!.Status.Should().Be("failed", "there is no SIP client in this codebase to place the call");
        sip.Detail.Should().NotContain("page sent");
    }

    /// <summary>
    /// The consequence that made it dangerous: the simulated success suppressed the
    /// nobody-was-contacted alarm, because that alarm is guarded on successCount == 0.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryChannelFailingAlwaysRaisesTheNobodyWasContactedAlarm(bool sipEnabled)
    {
        var (service, eventId, provider) = BuildFailingPipeline(sipEnabled);

        await service.ProcessDispatchJobAsync(eventId, "code-blue");

        var acknowledged = await StepAsync(provider, eventId, "acknowledged");
        acknowledged!.Status.Should().Be("failed");
        acknowledged.Detail.Should().Contain("DISPATCH FAILED");
        acknowledged.Detail.Should().Contain("nobody was");
    }

    /// <summary>
    /// A code call nobody answered must keep demanding attention. Acknowledging it is how
    /// it stops doing that in the Command Center.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACodeCallThatReachedNobodyIsNotAcknowledged(bool sipEnabled)
    {
        var (service, eventId, provider) = BuildFailingPipeline(sipEnabled);

        await service.ProcessDispatchJobAsync(eventId, "code-blue");

        var evt = await EventAsync(provider, eventId);
        evt.AcknowledgedAt.Should().BeNull(
            "an unanswered code call must stay active rather than being machine-acknowledged");
    }

    /// <summary>
    /// An operator who switched SIP on believes they have a fallback. Telling them it is
    /// "not configured" would be wrong; they configured it. They are told it does not
    /// exist.
    /// </summary>
    [Fact]
    public async Task EnablingSipInConfigurationSaysItIsNotImplemented()
    {
        var (service, eventId, provider) = BuildFailingPipeline(sipEnabled: true);

        await service.ProcessDispatchJobAsync(eventId, "code-blue");

        var sip = await StepAsync(provider, eventId, "sip_fallback");
        sip!.Detail.Should().Contain("not implemented");
        sip.Detail.Should().Contain("Escalate by phone");
    }

    [Fact]
    public async Task LeavingSipOffStillSaysManualDispatchIsNeeded()
    {
        var (service, eventId, provider) = BuildFailingPipeline(sipEnabled: false);

        await service.ProcessDispatchJobAsync(eventId, "code-blue");

        var sip = await StepAsync(provider, eventId, "sip_fallback");
        sip!.Status.Should().Be("failed");
        sip.Detail.Should().Contain("manual dispatch");
    }
}
