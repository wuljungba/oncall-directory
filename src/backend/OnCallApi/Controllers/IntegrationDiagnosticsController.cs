using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Models;
using OnCallApi.Services;
using OnCallApi.Services.Dispatch;
using OnCallApi.Validators;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/integrations/test")]
[Authorize(Policy = "RequireAdminFull")]
public class IntegrationDiagnosticsController : ControllerBase
{
    private readonly ICodeCallDispatchService _dispatch;
    private readonly ILogger<IntegrationDiagnosticsController> _logger;

    public IntegrationDiagnosticsController(
        ICodeCallDispatchService dispatch,
        ILogger<IntegrationDiagnosticsController> logger)
    {
        _dispatch = dispatch;
        _logger = logger;
    }

    /// <summary>Test all configured dispatch connections.</summary>
    [HttpPost("all")]
    public async Task<ActionResult<List<ConnectionStatus>>> TestAll()
    {
        _logger.LogInformation("Running pre-flight check for all dispatch channels");
        var results = await _dispatch.PreflightCheckAllAsync();
        return Ok(results);
    }

    /// <summary>Test CUCM AXL connection only.</summary>
    [HttpPost("cucm")]
    public async Task<ActionResult<ConnectionStatus>> TestCucm(
        [FromServices] ICiscoCucmClient cucm)
    {
        var status = await cucm.CheckConnectionAsync();
        return Ok(status);
    }

    /// <summary>Test InformaCast REST API connection only.</summary>
    [HttpPost("informacast")]
    public async Task<ActionResult<ConnectionStatus>> TestInformaCast(
        [FromServices] IInformaCastClient informaCast)
    {
        var status = await informaCast.CheckConnectionAsync();
        return Ok(status);
    }

    /// <summary>Test Vocera VMP API connection only.</summary>
    [HttpPost("vocera")]
    public async Task<ActionResult<ConnectionStatus>> TestVocera(
        [FromServices] IVoceraClient vocera)
    {
        var status = await vocera.CheckConnectionAsync();
        return Ok(status);
    }

    /// <summary>Test Twilio account credentials only. Sends nothing.</summary>
    [HttpPost("twilio")]
    public async Task<ActionResult<ConnectionStatus>> TestTwilio(
        [FromServices] ITwilioClient twilio,
        [FromServices] IOptions<DispatchOptions> options)
    {
        if (!options.Value.Twilio.Enabled)
        {
            return Ok(new ConnectionStatus
            {
                Connected = false,
                Detail = "Twilio is not enabled. Set Dispatch:Twilio:Enabled=true in app configuration.",
            });
        }

        var status = await twilio.CheckConnectionAsync();
        return Ok(status);
    }

    /// <summary>
    /// Sends a clearly-marked test SMS to a single number so an admin can confirm the
    /// channel end-to-end without firing a real code call. Audited: sending a text to an
    /// arbitrary number from the hospital's number is an outward-facing action.
    /// </summary>
    [HttpPost("twilio/send")]
    public async Task<ActionResult<object>> SendTestSms(
        [FromBody] TestSmsRequest request,
        [FromServices] ITwilioClient twilio,
        [FromServices] IOptions<DispatchOptions> options,
        [FromServices] IAuditService audit)
    {
        if (!options.Value.Twilio.Enabled)
            return BadRequest(new { error = "Twilio is not enabled. Set Dispatch:Twilio:Enabled=true." });

        var to = PhoneValidation.NormalizeToE164(request.ToPhone);
        if (to == null)
            return BadRequest(new { error = "A valid destination phone number is required." });

        var result = await twilio.SendSmsAsync(
            to,
            "[TEST] OnCall dispatch test message. No action needed — this is not a real code call.");

        // Record who sent a test and the outcome. The destination is deliberately not
        // logged in full; the Twilio message SID is the reference for follow-up.
        audit.Enqueue(new AuditLog
        {
            UserId = Guid.Empty,
            UserName = User.Identity?.Name ?? "unknown",
            Action = "Dispatched",
            ResourceType = "TwilioTestSms",
            ResourceId = result.IncidentId,
            Details = $"Admin test SMS to number ending {to[^Math.Min(4, to.Length)..]}; "
                    + $"success={result.Success}; detail={result.Detail}",
            Timestamp = DateTime.UtcNow,
        });

        if (!result.Success)
        {
            _logger.LogWarning("Twilio test SMS failed: {Detail}", result.Detail);
            return StatusCode(502, new { sent = false, detail = result.Detail });
        }

        return Ok(new { sent = true, messageSid = result.IncidentId, detail = result.Detail });
    }
}

public record TestSmsRequest(string ToPhone);
