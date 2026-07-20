using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/import")]
[Authorize(Policy = "RequireAdmin")]
public class ImportController : ControllerBase
{
    private readonly BulkImportService _importService;
    private readonly ILogger<ImportController> _logger;

    public ImportController(BulkImportService importService, ILogger<ImportController> logger)
    {
        _importService = importService;
        _logger = logger;
    }

    /// <summary>Dry-run validation of employee CSV import.</summary>
    [HttpPost("validate/employees")]
    public async Task<ActionResult<ImportResult>> ValidateEmployees(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "CSV file is required." });

        using var stream = file.OpenReadStream();
        var result = await _importService.ValidateEmployeesAsync(stream);
        return Ok(result);
    }

    /// <summary>Import employees from CSV. Creates new, updates existing by AzureAdObjectId.</summary>
    [HttpPost("employees")]
    public async Task<ActionResult<ImportResult>> ImportEmployees(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "CSV file is required." });

        _logger.LogInformation("Starting employee bulk import: {FileName}, {Size} bytes", file.FileName, file.Length);
        using var stream = file.OpenReadStream();
        var result = await _importService.ImportEmployeesAsync(stream);

        if (!result.IsValid)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>Dry-run validation of shift CSV import for a schedule.</summary>
    [HttpPost("validate/schedule/{scheduleId}")]
    public async Task<ActionResult<ImportResult>> ValidateShifts(int scheduleId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "CSV file is required." });

        using var stream = file.OpenReadStream();
        var result = await _importService.ValidateScheduleImportAsync(scheduleId, stream);
        return Ok(result);
    }

    /// <summary>Bulk assign shifts from CSV for a schedule.</summary>
    [HttpPost("schedule/{scheduleId}")]
    public async Task<ActionResult<ImportResult>> ImportShifts(int scheduleId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "CSV file is required." });

        _logger.LogInformation("Starting shift bulk import for schedule {ScheduleId}: {FileName}", scheduleId, file.FileName);
        using var stream = file.OpenReadStream();
        var result = await _importService.ImportShiftsAsync(scheduleId, stream);

        if (!result.IsValid)
            return BadRequest(result);

        return Ok(result);
    }
}
