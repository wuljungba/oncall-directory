using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace OnCallApi.Controllers;

/// <summary>
/// Collects Content-Security-Policy violation reports.
///
/// The policy is sent as Report-Only, which is the right first step — an enforcing
/// policy that misses one auth origin locks clinical staff out of the directory. But
/// Report-Only without a collector is the worst of both worlds: the browser evaluates
/// every violation and then throws it away, so the policy protects nothing and also
/// never accumulates the evidence needed to decide it is safe to enforce. This endpoint
/// is what turns the header from decoration into a measurement.
///
/// Anonymous by necessity — the browser posts these without credentials, and violations
/// on the login page happen before anyone is signed in.
/// </summary>
[ApiController]
[Route("api/public/csp-report")]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public class CspReportController : ControllerBase
{
    /// <summary>
    /// Reports are attacker-controllable and land in Log Analytics, which is capped at a
    /// daily quota. Truncate every field that a caller chooses the length of.
    /// </summary>
    private const int MaxFieldLength = 200;

    private const int MaxBodyBytes = 8 * 1024;

    private readonly ILogger<CspReportController> _logger;

    public CspReportController(ILogger<CspReportController> logger) => _logger = logger;

    [HttpPost]
    [EnableRateLimiting("CspReport")]
    public async Task<IActionResult> Report()
    {
        // 204 on every path, including malformed input: a browser cannot act on an error
        // here, and reporting must never become a way to probe the endpoint.
        if (Request.ContentLength > MaxBodyBytes)
            return NoContent();

        // Read one byte past the cap so an unset or lying Content-Length is caught by the
        // length check below rather than buffering whatever was actually sent.
        var buffer = new byte[MaxBodyBytes + 1];
        var read = await Request.Body.ReadAtLeastAsync(
            buffer, buffer.Length, throwOnEndOfStream: false, HttpContext.RequestAborted);

        if (read == 0 || read > MaxBodyBytes)
            return NoContent();

        try
        {
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, read));

            // report-uri wraps the payload in "csp-report"; some agents post it bare.
            var report = doc.RootElement.TryGetProperty("csp-report", out var wrapped)
                ? wrapped
                : doc.RootElement;

            var directive = Read(report, "effective-directive")
                            ?? Read(report, "violated-directive")
                            ?? "unknown";

            _logger.LogWarning(
                "CSP violation: directive={Directive} blocked={Blocked} document={Document}",
                directive,
                Read(report, "blocked-uri") ?? "unknown",
                Read(report, "document-uri") ?? "unknown");
        }
        catch (JsonException)
        {
            // Not worth a log line per malformed body — that is the flooding vector.
        }

        return NoContent();
    }

    private static string? Read(JsonElement report, string property) =>
        report.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? Truncate(value.GetString())
            : null;

    private static string? Truncate(string? value) =>
        value is null || value.Length <= MaxFieldLength ? value : value[..MaxFieldLength] + "…";
}
