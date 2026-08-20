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
/// Covers the Twilio leg of the code-call dispatch pipeline. The guardrail being
/// protected: an alert that cannot reach the on-call provider must be recorded as a
/// FAILED step, never skipped and never counted toward a successful dispatch. Directory
/// numbers are also not reliably E.164, so they must be normalized before they go out.
/// </summary>
public class CodeCallDispatchTwilioTests
{
    /// <summary>Records what the pipeline asked Twilio to send.</summary>
    private sealed class RecordingTwilioClient : ITwilioClient
    {
        public List<string> SentTo { get; } = new();
        public bool Succeed { get; set; } = true;

        public Task<ConnectionStatus> CheckConnectionAsync() =>
            Task.FromResult(new ConnectionStatus { Connected = true, Detail = "stub" });

        public Task<DispatchResult> SendSmsAsync(string toPhone, string message)
        {
            SentTo.Add(toPhone);
            return Task.FromResult(Succeed
                ? new DispatchResult { Success = true, IncidentId = "SMstub", Detail = "queued" }
                : new DispatchResult { Success = false, Detail = "rejected" });
        }
    }

    // The other three channels are disabled in these tests; the stubs exist only so the
    // pipeline's scope can resolve them.
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
    /// Builds the pipeline with only Twilio enabled, over an in-memory database seeded
    /// with an active primary shift whose holder carries <paramref name="mobilePhone"/>.
    /// </summary>
    private static (CodeCallDispatchService Service, RecordingTwilioClient Twilio, int EventId, IServiceProvider Provider)
        BuildPipeline(string? mobilePhone)
    {
        var dbName = Guid.NewGuid().ToString();
        var twilio = new RecordingTwilioClient();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<ICiscoCucmClient>(_ => new StubCucmClient());
        services.AddScoped<IInformaCastClient>(_ => new StubInformaCastClient());
        services.AddScoped<IVoceraClient>(_ => new StubVoceraClient());
        services.AddScoped<ITwilioClient>(_ => twilio);
        services.AddScoped<IPhoneTreeEventService>(sp => new PhoneTreeEventService(
            sp.GetRequiredService<AppDbContext>(), NullLogger<PhoneTreeEventService>.Instance));

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
                MobilePhone = mobilePhone,
            };
            var schedule = new Schedule { Id = 1, Name = "Cardiology call", DepartmentId = dept.Id };
            var now = DateTime.UtcNow;
            var shift = new Shift
            {
                Id = 1,
                ScheduleId = schedule.Id,
                EmployeeId = employee.Id,
                Tier = "primary",
                Status = "scheduled",
                StartTime = now.AddHours(-1),
                EndTime = now.AddHours(1),
            };
            var evt = new PhoneTreeEvent { Id = 1, PhoneTreeId = tree.Id, StartedAt = now, Location = "ICU 4" };

            db.Departments.Add(dept);
            db.PhoneTrees.Add(tree);
            db.Employees.Add(employee);
            db.Schedules.Add(schedule);
            db.Shifts.Add(shift);
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
        });

        var service = new CodeCallDispatchService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<OnCallApi.Hubs.OnCallNotificationHub>>(),
            options,
            new DispatchJobQueue(),
            NullLogger<CodeCallDispatchService>.Instance);

        return (service, twilio, eventId, provider);
    }

    private static async Task<DispatchStep?> GetSmsStepAsync(IServiceProvider provider, int eventId)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DispatchSteps
            .Where(s => s.PhoneTreeEventId == eventId && s.StepKey == "twilio_sms")
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync();
    }

    [Theory]
    [InlineData("(202) 555-0134", "+12025550134")]
    [InlineData("202-555-0134", "+12025550134")]
    [InlineData("+1 202 555 0134", "+12025550134")]
    [InlineData("+12025550134", "+12025550134")]
    public async Task Dispatch_NormalizesDirectoryNumberToE164BeforeSending(string stored, string expected)
    {
        var (service, twilio, eventId, provider) = BuildPipeline(stored);

        await service.ProcessDispatchJobAsync(eventId, "code-blue");

        twilio.SentTo.Should().ContainSingle().Which.Should().Be(expected);

        var step = await GetSmsStepAsync(provider, eventId);
        step!.Status.Should().Be("completed");
        step.ProviderMessageId.Should().Be("SMstub");
    }

    [Fact]
    public async Task Dispatch_WithNoMobileNumber_RecordsFailedStepNotSkipped()
    {
        var (service, twilio, eventId, provider) = BuildPipeline(null);

        await service.ProcessDispatchJobAsync(eventId, "code-blue");

        twilio.SentTo.Should().BeEmpty();

        var step = await GetSmsStepAsync(provider, eventId);
        step!.Status.Should().Be("failed");
        step.Detail.Should().Contain("No on-call provider mobile number");
    }

    /// <summary>
    /// An internal extension is not an SMS destination. Best-effort normalization would
    /// turn "ext. 4412" into the well-formed but unroutable "+14412"; sending there loses
    /// the alert silently, so the dispatch must fail instead.
    /// </summary>
    [Theory]
    [InlineData("ext. 4412")]
    [InlineData("4412")]
    [InlineData("x88")]
    public async Task Dispatch_WithExtensionRatherThanMobile_RecordsFailedStep(string stored)
    {
        var (service, twilio, eventId, provider) = BuildPipeline(stored);

        await service.ProcessDispatchJobAsync(eventId, "code-blue");

        twilio.SentTo.Should().BeEmpty();

        var step = await GetSmsStepAsync(provider, eventId);
        step!.Status.Should().Be("failed");
        step.Detail.Should().Contain("not a valid phone number");
    }

    [Fact]
    public async Task Dispatch_WhenTwilioRejectsTheMessage_RecordsFailedStep()
    {
        var (service, twilio, eventId, provider) = BuildPipeline("+12025550134");
        twilio.Succeed = false;

        await service.ProcessDispatchJobAsync(eventId, "code-blue");

        var step = await GetSmsStepAsync(provider, eventId);
        step!.Status.Should().Be("failed");
        step.Detail.Should().Contain("rejected");
    }
}
