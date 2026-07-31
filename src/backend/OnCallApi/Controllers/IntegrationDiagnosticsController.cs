using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnCallApi.Services;
using OnCallApi.Services.Dispatch;

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
}
