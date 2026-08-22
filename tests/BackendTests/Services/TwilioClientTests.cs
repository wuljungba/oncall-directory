using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Services.Dispatch;

namespace BackendTests.Services;

/// <summary>
/// Covers the wire format of the Twilio send: the delivery-status callback must be
/// attached (without it an undelivered code-call SMS is invisible), a messaging service
/// must take precedence over a bare sender number, and a rejected send must never come
/// back as a success.
/// </summary>
public class TwilioClientTests
{
    /// <summary>Captures the outgoing request and replays a canned response.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public string? CapturedContent { get; private set; }
        public Uri? CapturedUri { get; private set; }

        public CapturingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedUri = request.RequestUri;
            CapturedContent = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static TwilioClient Build(TwilioOptions options, HttpMessageHandler handler)
    {
        var dispatch = new DispatchOptions { Twilio = options };
        return new TwilioClient(
            new SingleClientFactory(handler),
            Options.Create(dispatch),
            NullLogger<TwilioClient>.Instance);
    }

    private static TwilioOptions BaseOptions() => new()
    {
        Enabled = true,
        AccountSid = "ACtest",
        AuthToken = "token",
        FromNumber = "+12025550100",
    };

    [Fact]
    public async Task SendSms_IncludesStatusCallback_WhenConfigured()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created, """{"sid":"SM1","status":"queued"}""");
        var options = BaseOptions();
        options.StatusCallbackUrl = "https://oncall.test/api/public/twilio/status";

        var result = await Build(options, handler).SendSmsAsync("+12025550111", "code blue");

        result.Success.Should().BeTrue();
        result.IncidentId.Should().Be("SM1");
        Uri.UnescapeDataString(handler.CapturedContent!)
            .Should().Contain("StatusCallback=https://oncall.test/api/public/twilio/status");
    }

    [Fact]
    public async Task SendSms_OmitsStatusCallback_WhenNotConfigured()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created, """{"sid":"SM2","status":"queued"}""");

        var result = await Build(BaseOptions(), handler).SendSmsAsync("+12025550111", "code blue");

        result.Success.Should().BeTrue();
        handler.CapturedContent.Should().NotContain("StatusCallback");
        // The caller must be able to tell that no delivery confirmation is coming.
        result.Detail.Should().Contain("no delivery confirmation");
    }

    [Fact]
    public async Task SendSms_PrefersMessagingService_OverFromNumber()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created, """{"sid":"SM3","status":"queued"}""");
        var options = BaseOptions();
        options.MessagingServiceSid = "MGtest";

        await Build(options, handler).SendSmsAsync("+12025550111", "code blue");

        var body = Uri.UnescapeDataString(handler.CapturedContent!);
        body.Should().Contain("MessagingServiceSid=MGtest");
        body.Should().NotContain("From=");
    }

    [Fact]
    public async Task SendSms_UsesFromNumber_WhenNoMessagingService()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created, """{"sid":"SM4","status":"queued"}""");

        await Build(BaseOptions(), handler).SendSmsAsync("+12025550111", "code blue");

        var body = Uri.UnescapeDataString(handler.CapturedContent!);
        body.Should().Contain("From=+12025550100");
        body.Should().NotContain("MessagingServiceSid");
    }

    [Fact]
    public async Task SendSms_OnTwilioError_ReportsFailureWithTwilioDetail()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.BadRequest,
            """{"code":21608,"message":"The number is unverified. Trial accounts cannot send messages to unverified numbers."}""");

        var result = await Build(BaseOptions(), handler).SendSmsAsync("+12025550111", "code blue");

        result.Success.Should().BeFalse();
        result.Detail.Should().Contain("unverified").And.Contain("21608");
    }

    /// <summary>
    /// Regression: BaseAddress was "https://api.twilio.com/2010-04-01" with no trailing slash,
    /// so URI resolution replaced the version segment and every send went to
    /// https://api.twilio.com/Accounts/... — which Twilio rejects with 400 before it looks at
    /// the credentials. The channel appeared configured and failed on every code call.
    /// </summary>
    [Fact]
    public async Task SendSms_PostsToVersionedMessagesEndpoint()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created, """{"sid":"SM5","status":"queued"}""");

        await Build(BaseOptions(), handler).SendSmsAsync("+12025550111", "code blue");

        handler.CapturedUri!.ToString()
            .Should().Be("https://api.twilio.com/2010-04-01/Accounts/ACtest/Messages.json");
    }

    [Fact]
    public async Task CheckConnection_GetsVersionedAccountEndpoint()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"sid":"ACtest","status":"active"}""");

        var status = await Build(BaseOptions(), handler).CheckConnectionAsync();

        status.Connected.Should().BeTrue();
        handler.CapturedUri!.ToString()
            .Should().Be("https://api.twilio.com/2010-04-01/Accounts/ACtest.json");
    }

    [Fact]
    public async Task SendSms_OnTransportFailure_ReportsFailureRatherThanThrowing()
    {
        var handler = new ThrowingHandler();

        var result = await Build(BaseOptions(), handler).SendSmsAsync("+12025550111", "code blue");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeEmpty();
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
