using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Hubs;
using OnCallApi.Models;

namespace OnCallApi.Controllers;

/// <summary>
/// Receives Twilio delivery-status callbacks for code-call SMS alerts.
///
/// Twilio's 201 response to a send only means the message was *queued*. Delivery
/// (or a carrier rejection, an unregistered A2P campaign, a disconnected handset)
/// is reported asynchronously to this endpoint. Without it, an SMS that never
/// reached the on-call provider would be recorded as a successful dispatch —
/// exactly the kind of silent failure the code-call path must not have.
///
/// Placed under /api/public (outside the JWT-protected prefixes) and authenticated
/// by Twilio's own request signature, which is an HMAC keyed by the account's Auth
/// Token — the same shared-secret model as <see cref="EhrWebhookController"/>.
/// </summary>
[ApiController]
[Route("api/public/twilio")]
[AllowAnonymous]
public class TwilioStatusWebhookController : ControllerBase
{
    /// <summary>Terminal Twilio statuses that mean the alert did NOT reach the provider.</summary>
    private static readonly HashSet<string> FailureStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "undelivered", "failed", "canceled" };

    private readonly TwilioOptions _options;
    private readonly AppDbContext _db;
    private readonly IHubContext<OnCallNotificationHub> _hub;
    private readonly ILogger<TwilioStatusWebhookController> _logger;

    public TwilioStatusWebhookController(
        IOptions<DispatchOptions> options,
        AppDbContext db,
        IHubContext<OnCallNotificationHub> hub,
        ILogger<TwilioStatusWebhookController> logger)
    {
        _options = options.Value.Twilio;
        _db = db;
        _hub = hub;
        _logger = logger;
    }

    [HttpPost("status")]
    public async Task<IActionResult> MessageStatus()
    {
        // Without an Auth Token there is no way to authenticate the caller, so the
        // endpoint refuses rather than accepting unverified dispatch outcomes.
        if (string.IsNullOrWhiteSpace(_options.AuthToken) ||
            _options.AuthToken.Contains("your-twilio", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(503, new { error = "Twilio is not configured. Set Dispatch:Twilio:AuthToken." });
        }

        if (!Request.HasFormContentType)
            return BadRequest(new { error = "Expected application/x-www-form-urlencoded." });

        var form = await Request.ReadFormAsync();

        if (!IsValidTwilioSignature(form))
        {
            _logger.LogWarning(
                "Twilio status callback rejected: invalid X-Twilio-Signature (from {IP})",
                HttpContext.Connection.RemoteIpAddress);
            return StatusCode(403, new { error = "Invalid request signature." });
        }

        var messageSid = form["MessageSid"].FirstOrDefault() ?? form["SmsSid"].FirstOrDefault();
        var messageStatus = form["MessageStatus"].FirstOrDefault() ?? form["SmsStatus"].FirstOrDefault();
        var errorCode = form["ErrorCode"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(messageSid) || string.IsNullOrWhiteSpace(messageStatus))
            return BadRequest(new { error = "MessageSid and MessageStatus are required." });

        // Correlate back to the dispatch step that sent this message.
        var step = await _db.DispatchSteps
            .FirstOrDefaultAsync(s => s.ProviderMessageId == messageSid);

        if (step == null)
        {
            // Test sends and messages from a previous deployment have no step. Not an
            // error — acknowledge so Twilio stops retrying.
            _logger.LogInformation(
                "Twilio status '{Status}' for message {Sid} has no matching dispatch step; ignoring",
                messageStatus, messageSid);
            return Ok();
        }

        var isFailure = FailureStatuses.Contains(messageStatus);
        var isDelivered = string.Equals(messageStatus, "delivered", StringComparison.OrdinalIgnoreCase);

        // Intermediate statuses (queued, sending, sent) carry no new information about
        // whether the provider was actually reached.
        if (!isFailure && !isDelivered)
            return Ok();

        step.Status = isFailure ? "failed" : "completed";
        step.CompletedAt = DateTime.UtcNow;
        step.Detail = isFailure
            ? $"SMS to on-call provider NOT delivered (Twilio status '{messageStatus}'"
              + (string.IsNullOrWhiteSpace(errorCode) ? ")" : $", error {errorCode})")
            : "SMS to on-call provider delivered";

        await _db.SaveChangesAsync();

        if (isFailure)
        {
            // Safety-critical: an undelivered code-call alert must be loud, not a log line.
            _logger.LogError(
                "Code-call SMS NOT delivered for event {EventId} (step {StepId}): Twilio status '{Status}', error {ErrorCode}",
                step.PhoneTreeEventId, step.Id, messageStatus, errorCode ?? "none");
        }

        await BroadcastAsync(step);

        return Ok();
    }

    /// <summary>
    /// Pushes the settled step to the Command Center, tenant-scoped where the tenant can
    /// be resolved. Mirrors CodeCallDispatchService's DispatchStepCompleted broadcast.
    /// </summary>
    private async Task BroadcastAsync(DispatchStep step)
    {
        var payload = new
        {
            eventId = step.PhoneTreeEventId,
            stepKey = step.StepKey,
            status = step.Status,
            detail = step.Detail,
            completedAt = step.CompletedAt ?? DateTime.UtcNow,
        };

        var tenantId = await _db.PhoneTreeEvents
            .Where(e => e.Id == step.PhoneTreeEventId)
            .Select(e => e.PhoneTree!.Department!.TenantId)
            .FirstOrDefaultAsync();

        if (tenantId.HasValue)
            await _hub.Clients.Group($"tenant-{tenantId.Value}").SendAsync("DispatchStepCompleted", payload);
        else
            await _hub.Clients.All.SendAsync("DispatchStepCompleted", payload);
    }

    /// <summary>
    /// Validates Twilio's X-Twilio-Signature per their documented scheme: HMAC-SHA1,
    /// keyed by the account Auth Token, over the full request URL with every POST
    /// parameter appended as key+value in alphabetical key order, base64-encoded.
    ///
    /// The signed URL is the one configured on the message (StatusCallbackUrl) — that
    /// is what Twilio hashed. Reconstructing it from the incoming request would break
    /// behind App Service's TLS-terminating proxy, where the inbound scheme/host can
    /// differ from the public one.
    /// </summary>
    private bool IsValidTwilioSignature(IFormCollection form)
    {
        var provided = Request.Headers["X-Twilio-Signature"].FirstOrDefault();
        if (string.IsNullOrEmpty(provided)) return false;

        var url = !string.IsNullOrWhiteSpace(_options.StatusCallbackUrl)
            ? _options.StatusCallbackUrl
            : $"{Request.Scheme}://{Request.Host}{Request.Path}{Request.QueryString}";

        var builder = new StringBuilder(url);
        foreach (var key in form.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            builder.Append(key);
            builder.Append(form[key].ToString());
        }

        var expected = Convert.ToBase64String(HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(_options.AuthToken),
            Encoding.UTF8.GetBytes(builder.ToString())));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(provided));
    }
}
