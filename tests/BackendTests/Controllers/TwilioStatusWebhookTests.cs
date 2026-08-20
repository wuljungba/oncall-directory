using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OnCallApi.Data;
using OnCallApi.Models;

namespace BackendTests.Controllers;

/// <summary>
/// Guards the Twilio delivery-status callback. Two properties matter:
///   1. Only Twilio can write dispatch outcomes (signature gate), and the endpoint
///      refuses entirely when no Auth Token is configured.
///   2. A message Twilio reports as undelivered flips the dispatch step to FAILED —
///      an SMS that never reached the on-call provider must never read as success.
/// </summary>
[Collection(WebHostCollection.Name)]
public class TwilioStatusWebhookTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string AuthToken = "test-twilio-auth-token";
    private const string CallbackUrl = "https://oncall.test/api/public/twilio/status";

    private readonly WebApplicationFactory<Program> _factory;

    public TwilioStatusWebhookTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Dispatch:Twilio:Enabled"] = "true",
                    ["Dispatch:Twilio:AccountSid"] = "ACtest",
                    ["Dispatch:Twilio:AuthToken"] = AuthToken,
                    ["Dispatch:Twilio:FromNumber"] = "+12025550100",
                    ["Dispatch:Twilio:StatusCallbackUrl"] = CallbackUrl,
                })));
    }

    /// <summary>Twilio's scheme: HMAC-SHA1 over the URL + POST params in key order.</summary>
    private static string Sign(string url, IEnumerable<KeyValuePair<string, string>> fields, string token)
    {
        var sb = new StringBuilder(url);
        foreach (var kv in fields.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key);
            sb.Append(kv.Value);
        }
        return Convert.ToBase64String(
            HMACSHA1.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private HttpRequestMessage Build(
        Dictionary<string, string> fields, string? signature, string signingToken = AuthToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/public/twilio/status")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        req.Headers.Add("X-Twilio-Signature", signature ?? Sign(CallbackUrl, fields, signingToken));
        return req;
    }

    /// <summary>
    /// A message SID unique to this run. The dev/test database is a persistent SQLite
    /// file, so a fixed SID would collide with steps left behind by earlier runs and the
    /// webhook would settle the wrong row.
    /// </summary>
    private static string NewSid() => $"SM{Guid.NewGuid():N}"[..34];

    /// <summary>Seeds a dispatch step that claims an SMS was sent, and returns its id.</summary>
    private async Task<int> SeedSentSmsStepAsync(string messageSid)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tree = new PhoneTree { Name = $"Test tree {messageSid}", TreeType = "code-blue" };
        db.PhoneTrees.Add(tree);
        await db.SaveChangesAsync();

        var evt = new PhoneTreeEvent { PhoneTreeId = tree.Id, StartedAt = DateTime.UtcNow, Location = "Test Bay" };
        db.PhoneTreeEvents.Add(evt);
        await db.SaveChangesAsync();

        var step = new DispatchStep
        {
            PhoneTreeEventId = evt.Id,
            StepKey = "twilio_sms",
            Status = "completed",
            CompletedAt = DateTime.UtcNow,
            Detail = "SMS queued — awaiting delivery confirmation",
            ProviderMessageId = messageSid,
        };
        db.DispatchSteps.Add(step);
        await db.SaveChangesAsync();

        return step.Id;
    }

    private async Task<DispatchStep> GetStepAsync(int stepId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DispatchSteps.AsNoTracking().FirstAsync(s => s.Id == stepId);
    }

    [Fact]
    public async Task Status_WithoutSignature_IsForbidden()
    {
        var client = _factory.CreateClient();
        var fields = new Dictionary<string, string>
        {
            ["MessageSid"] = "SMnosig",
            ["MessageStatus"] = "delivered",
        };

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/public/twilio/status")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Status_WithSignatureFromWrongToken_IsForbidden()
    {
        var client = _factory.CreateClient();
        var fields = new Dictionary<string, string>
        {
            ["MessageSid"] = "SMwrongtoken",
            ["MessageStatus"] = "delivered",
        };

        var response = await client.SendAsync(Build(fields, signature: null, signingToken: "not-the-token"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Status_WithTamperedBody_IsForbidden()
    {
        var client = _factory.CreateClient();
        var signed = new Dictionary<string, string>
        {
            ["MessageSid"] = "SMtampered",
            ["MessageStatus"] = "delivered",
        };
        var signature = Sign(CallbackUrl, signed, AuthToken);

        // Same signature, different payload — a failure re-labelled as a delivery.
        var tampered = new Dictionary<string, string>
        {
            ["MessageSid"] = "SMtampered",
            ["MessageStatus"] = "undelivered",
        };

        var response = await client.SendAsync(Build(tampered, signature));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Status_Undelivered_MarksStepFailed()
    {
        var sid = NewSid();
        var stepId = await SeedSentSmsStepAsync(sid);
        var client = _factory.CreateClient();

        var fields = new Dictionary<string, string>
        {
            ["MessageSid"] = sid,
            ["MessageStatus"] = "undelivered",
            ["ErrorCode"] = "30006",
        };

        var response = await client.SendAsync(Build(fields, signature: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var step = await GetStepAsync(stepId);
        step.Status.Should().Be("failed");
        step.Detail.Should().Contain("NOT delivered").And.Contain("30006");
    }

    [Fact]
    public async Task Status_Delivered_SettlesStepAsCompleted()
    {
        var sid = NewSid();
        var stepId = await SeedSentSmsStepAsync(sid);
        var client = _factory.CreateClient();

        var fields = new Dictionary<string, string>
        {
            ["MessageSid"] = sid,
            ["MessageStatus"] = "delivered",
        };

        var response = await client.SendAsync(Build(fields, signature: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var step = await GetStepAsync(stepId);
        step.Status.Should().Be("completed");
        step.Detail.Should().Contain("delivered");
    }

    [Fact]
    public async Task Status_IntermediateStatus_LeavesStepUntouched()
    {
        var sid = NewSid();
        var stepId = await SeedSentSmsStepAsync(sid);
        var client = _factory.CreateClient();

        var fields = new Dictionary<string, string>
        {
            ["MessageSid"] = sid,
            ["MessageStatus"] = "sending",
        };

        var response = await client.SendAsync(Build(fields, signature: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var step = await GetStepAsync(stepId);
        step.Detail.Should().Contain("awaiting delivery confirmation");
    }

    [Fact]
    public async Task Status_ForUnknownMessage_IsAcknowledgedNotAnError()
    {
        var client = _factory.CreateClient();
        var fields = new Dictionary<string, string>
        {
            ["MessageSid"] = NewSid(),
            ["MessageStatus"] = "delivered",
        };

        var response = await client.SendAsync(Build(fields, signature: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>
/// With no Auth Token configured there is no way to authenticate Twilio, so the endpoint
/// must refuse rather than accept unverified dispatch outcomes.
/// </summary>
[Collection(WebHostCollection.Name)]
public class TwilioStatusWebhookUnconfiguredTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TwilioStatusWebhookUnconfiguredTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Dispatch:Twilio:Enabled"] = "false",
                    ["Dispatch:Twilio:AuthToken"] = "your-twilio-auth-token",
                })));
    }

    [Fact]
    public async Task Status_WhenTwilioUnconfigured_IsServiceUnavailable()
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/public/twilio/status")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["MessageSid"] = "SMx",
                ["MessageStatus"] = "delivered",
            }),
        };

        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }
}
