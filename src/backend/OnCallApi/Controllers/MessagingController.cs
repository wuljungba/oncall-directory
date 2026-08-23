using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;
using OnCallApi.Services.Dispatch;
using OnCallApi.Validators;

namespace OnCallApi.Controllers;

/// <summary>
/// Direct SMS to a provider — the "text the person who is on call right now" path, as
/// opposed to the automatic code-call broadcast in <see cref="Services.CodeCallDispatchService"/>.
///
/// Gated on CodeCall.Write rather than Directory.Read: this spends money, leaves the
/// building, and arrives on a clinician's personal handset, so it is a dispatch-class
/// action and not a directory lookup.
///
/// SMS is NOT a secure channel and carries no encryption in transit that we control, so
/// the message body is never written to the audit log and the UI warns against PHI. The
/// audit row records who texted whom, and the Twilio message SID for follow-up.
/// </summary>
[ApiController]
[Route("api/messaging")]
[Authorize(Policy = "RequireCodeCallWrite")]
public class MessagingController : ControllerBase
{
    /// <summary>Two SMS segments. Long enough to be useful, short enough to discourage notes.</summary>
    private const int MaxMessageLength = 300;

    private readonly AppDbContext _db;
    private readonly ITenantScope _tenantScope;
    private readonly ILogger<MessagingController> _logger;

    public MessagingController(
        AppDbContext db,
        ITenantScope tenantScope,
        ILogger<MessagingController> logger)
    {
        _db = db;
        _tenantScope = tenantScope;
        _logger = logger;
    }

    /// <summary>
    /// Sends an SMS to one employee's mobile number. Every failure mode is reported
    /// explicitly — a message the provider never received must never look like a success.
    /// </summary>
    [HttpPost("sms/{employeeId:guid}")]
    public async Task<ActionResult<SendSmsResponse>> SendSms(
        Guid employeeId,
        [FromBody] SendProviderSmsRequest request,
        [FromServices] ITwilioClient twilio,
        [FromServices] IOptions<DispatchOptions> options,
        [FromServices] IAuditService audit)
    {
        if (!options.Value.Twilio.Enabled)
        {
            // 503, not 400: the request was fine, the capability is switched off. Saying so
            // plainly stops "nothing happened" being mistaken for "message sent".
            return StatusCode(503, new SendSmsResponse(false,
                "SMS is not configured on this server, so no message was sent."));
        }

        var body = (request?.Message ?? "").Trim();
        if (body.Length == 0)
            return BadRequest(new SendSmsResponse(false, "A message is required."));
        if (body.Length > MaxMessageLength)
            return BadRequest(new SendSmsResponse(false,
                $"Message is {body.Length} characters; the limit is {MaxMessageLength}."));

        // Tenant scoping, so a scoped admin cannot text another organisation's staff.
        var allowedTenantIds = await _tenantScope.AllowedTenantIdsAsync();
        var employee = await _db.Employees
            .Where(e => e.Id == employeeId)
            .Where(e => allowedTenantIds == null || (e.TenantId != null && allowedTenantIds.Contains(e.TenantId.Value)))
            .FirstOrDefaultAsync();

        if (employee == null)
            return NotFound(new SendSmsResponse(false, "No such person in the directory."));

        // NormalizeToDialable, not NormalizeToE164 — the same call the code-call dispatch
        // makes. Plain E.164 normalization is best-effort for directory display and will
        // happily promote the extension "4412" to "+14412", which sends the message nowhere
        // and reports success.
        var destination = PhoneValidation.NormalizeToDialable(employee.MobilePhone);
        if (destination == null)
        {
            var reason = string.IsNullOrWhiteSpace(employee.MobilePhone)
                ? "This person has no mobile number on file, so no message was sent."
                : "This person's mobile number is not a valid phone number, so no message was sent.";
            _logger.LogWarning(
                "SMS to employee {EmployeeId} not sent: unusable mobile number", employeeId);
            return UnprocessableEntity(new SendSmsResponse(false, reason));
        }

        // Name the sender: an unattributed text from an unknown number gets ignored, which
        // on an on-call escalation is the same as not sending it.
        var senderName = User.Identity?.Name ?? "OnCall";
        var result = await twilio.SendSmsAsync(destination, $"{body}\n— {senderName} (OnCall)");

        // Deliberately no message body and no phone number in the audit row: SMS is an
        // insecure channel, and copying its contents into a six-year audit store would
        // spread anything sensitive rather than contain it.
        audit.Enqueue(new AuditLog
        {
            UserId = Guid.Empty,
            UserName = senderName,
            Action = "Dispatched",
            ResourceType = "ProviderSms",
            ResourceId = employeeId.ToString(),
            Details = $"Direct SMS to employee {employeeId}; {body.Length} chars; "
                    + $"success={result.Success}; sid={result.IncidentId}; detail={result.Detail}",
            Timestamp = DateTime.UtcNow,
        });

        if (!result.Success)
        {
            _logger.LogWarning(
                "SMS to employee {EmployeeId} failed: {Detail}", employeeId, result.Detail);
            return StatusCode(502, new SendSmsResponse(false, result.Detail ?? "Twilio rejected the message."));
        }

        _logger.LogInformation(
            "SMS sent to employee {EmployeeId} (SID {Sid})", employeeId, result.IncidentId);

        return Ok(new SendSmsResponse(true, result.Detail ?? "Message accepted by Twilio.", result.IncidentId));
    }
}

public record SendProviderSmsRequest(string? Message);

/// <summary>
/// <paramref name="Detail"/> is shown to the operator verbatim, so it always says whether
/// the message went anywhere.
/// </summary>
public record SendSmsResponse(bool Sent, string Detail, string? MessageSid = null);
