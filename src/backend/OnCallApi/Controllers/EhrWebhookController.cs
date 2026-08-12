using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

/// <summary>
/// Machine-to-machine entry point for an external EHR (e.g., Epic) to launch the on-call /
/// code-call communication workflow. Placed under /api/public (outside the JWT protected
/// prefixes) and authenticated by a shared secret — either an API key header or an
/// HMAC-SHA256 signature over the request body.
///
/// Safety: starting a code call fires real dispatch channels. This endpoint is the
/// equivalent consent gate for EHR-triggered launches — the shared secret is mandatory,
/// and a placeholder/absent secret causes the endpoint to refuse (503) rather than fire.
/// </summary>
[ApiController]
[Route("api/public/ehr")]
[AllowAnonymous]
public class EhrWebhookController : ControllerBase
{
    private readonly string _webhookKey;
    private readonly AppDbContext _db;
    private readonly IPhoneTreeEventService _events;
    private readonly ICodeCallDispatchService _dispatch;
    private readonly IAuditService _audit;
    private readonly ILogger<EhrWebhookController> _logger;

    public EhrWebhookController(
        IConfiguration config,
        AppDbContext db,
        IPhoneTreeEventService events,
        ICodeCallDispatchService dispatch,
        IAuditService audit,
        ILogger<EhrWebhookController> logger)
    {
        _webhookKey = config["Authentication:EhrWebhook:Key"] ?? "";
        _db = db;
        _events = events;
        _dispatch = dispatch;
        _audit = audit;
        _logger = logger;
    }

    [HttpPost("on-call")]
    public async Task<ActionResult<object>> LaunchOnCall()
    {
        if (string.IsNullOrWhiteSpace(_webhookKey) || _webhookKey.Contains("change-me", StringComparison.OrdinalIgnoreCase))
            return StatusCode(503, new { error = "EHR webhook is not configured. Set Authentication:EhrWebhook:Key." });

        // Read the raw body once and use the SAME bytes for signature verification and
        // deserialization. (Reading via [FromBody] would consume the stream first.)
        Request.EnableBuffering();
        Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync();
        Request.Body.Position = 0;

        // Authenticate: accept either an API key header or an HMAC signature over the body.
        if (!await IsAuthorizedAsync(rawBody))
            return Unauthorized(new { error = "Invalid or missing signature." });

        EhrLaunchRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<EhrLaunchRequest>(rawBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Malformed JSON body." });
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Location))
            return BadRequest(new { error = "A location is required." });

        // Resolve the phone tree by the requested code type or department name.
        var tree = await ResolvePhoneTreeAsync(request);
        if (tree == null)
            return NotFound(new { error = "No active phone tree matches the requested code type/department." });

        var evt = new PhoneTreeEvent
        {
            PhoneTreeId = tree.Id,
            StartedAt = DateTime.UtcNow,
            Location = request.Location,
            LocationZone = request.LocationZone,
            Notes = request.Notes,
            RequestedByName = request.RequestedByName,
            ExternalIncidentId = request.ExternalIncidentId,
        };

        var created = await _events.CreateEventAsync(evt);

        var codeType = created.PhoneTree?.TreeType ?? "emergency";
        await _dispatch.DispatchIncidentAsync(created.Id, codeType);

        _audit.Enqueue(new OnCallApi.Models.AuditLog
        {
            UserId = Guid.Empty,
            UserName = "EHR-WEBHOOK",
            Action = "Created",
            ResourceType = "PhoneTreeEvent",
            ResourceId = created.Id.ToString(),
            Details = $"EHR-launched {codeType} at {request.Location}; externalId={request.ExternalIncidentId ?? ""}",
            Timestamp = DateTime.UtcNow,
        });

        _logger.LogInformation("EHR webhook launched on-call event {Id} ({Code}) at {Location}",
            created.Id, codeType, request.Location);

        return CreatedAtAction(nameof(LaunchOnCall), new { eventId = created.Id },
            new { eventId = created.Id, status = "created", externalIncidentId = request.ExternalIncidentId });
    }

    /// <summary>Accept an API key header, or verify an HMAC-SHA256 signature over the raw body.</summary>
    private Task<bool> IsAuthorizedAsync(string rawBody)
    {
        // API key header (constant-time compare)
        var keyHeader = Request.Headers["X-Ehr-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(keyHeader))
            return Task.FromResult(CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(keyHeader),
                Encoding.UTF8.GetBytes(_webhookKey)));

        // HMAC signature header = base64(HMAC-SHA256(secret, rawBody))
        var sigHeader = Request.Headers["X-Ehr-Signature"].FirstOrDefault();
        if (string.IsNullOrEmpty(sigHeader)) return Task.FromResult(false);
        try
        {
            var expected = Convert.ToBase64String(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(_webhookKey), Encoding.UTF8.GetBytes(rawBody)));

            return Task.FromResult(CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(sigHeader)));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    private async Task<PhoneTree?> ResolvePhoneTreeAsync(EhrLaunchRequest request)
    {
        var q = _db.PhoneTrees.Where(t => t.IsActive);

        if (!string.IsNullOrWhiteSpace(request.CodeType))
        {
            var match = await q.FirstOrDefaultAsync(t => t.TreeType == request.CodeType);
            if (match != null) return match;
        }

        if (!string.IsNullOrWhiteSpace(request.DepartmentName))
        {
            var dept = await _db.Departments.FirstOrDefaultAsync(d => d.Name.ToLower() == request.DepartmentName.ToLower());
            if (dept != null)
            {
                var byDept = await q.FirstOrDefaultAsync(t => t.DepartmentId == dept.Id);
                if (byDept != null) return byDept;
            }
        }

        // Fall back to a generic "oncall"/first active tree, then emergency.
        return await q.FirstOrDefaultAsync(t => t.TreeType == "oncall")
            ?? await q.FirstOrDefaultAsync(t => t.TreeType == "emergency");
    }
}

public class EhrLaunchRequest
{
    public string? CodeType { get; set; }
    public string? DepartmentName { get; set; }
    public string Location { get; set; } = "";
    public string? LocationZone { get; set; }
    public string? Notes { get; set; }
    public string? RequestedByName { get; set; }
    public string? ExternalIncidentId { get; set; }
}