using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OnCallApi.Services;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/import")]
public class ImportController : ControllerBase
{
    private readonly BulkImportService _importService;
    private readonly ITenantContextService _tenants;
    private readonly ILogger<ImportController> _logger;

    public ImportController(BulkImportService importService, ITenantContextService tenants, ILogger<ImportController> logger)
    {
        _importService = importService;
        _tenants = tenants;
        _logger = logger;
    }

    /// <summary>Dry-run validation of employee CSV import.</summary>
    [HttpPost("validate/employees")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public async Task<ActionResult<ImportResult>> ValidateEmployees(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ImportResult { Errors = ["CSV file is required."], IsValid = false });

        try
        {
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            memoryStream.Position = 0;
            _logger.LogInformation("Starting employee validation: {FileName}, {Size} bytes", file.FileName, file.Length);
            var result = await _importService.ValidateEmployeesAsync(memoryStream);
            _logger.LogInformation("Validation result: {TotalRows} rows, {Errors} errors", result.TotalRows, result.Errors.Count);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating employee CSV: {FileName}, {Exception}", file.FileName, ex.ToString());
            return Ok(new ImportResult
            {
                Errors = [$"Validation error: {ex.GetType().Name}: {ex.Message}"],
                IsValid = false
            });
        }
    }

    /// <summary>
    /// Import employees from CSV. Creates new, updates existing by AzureAdObjectId.
    /// Optional tenantId scopes imported users to a subscription (super admin onboarding);
    /// sub-admins are scoped to their own tenant by the service.
    /// </summary>
    [HttpPost("employees")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public async Task<ActionResult<ImportResult>> ImportEmployees(IFormFile file, [FromQuery] int? tenantId = null)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ImportResult { Errors = ["CSV file is required."], IsValid = false });

        _logger.LogInformation("Starting employee bulk import: {FileName}, {Size} bytes", file.FileName, file.Length);
        try
        {
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            // Tenant scoping: a super admin may import into the requested subscription;
            // any other caller (sub-admin / Directory.Write) is forced to their own tenant
            // regardless of the query param — prevents cross-tenant employee writes.
            var isSuperAdmin = _tenants.IsSuperAdmin(User);
            var effectiveTenantId = isSuperAdmin
                ? tenantId
                : (await _tenants.GetAuthorizedTenantIdsAsync(User)).FirstOrDefault();

            // Which existing records this caller may match against. Null means a super
            // admin (unrestricted); anyone else can only touch their own tenants, so an
            // upload cannot reach across into another customer's directory.
            var allowedTenantIds = isSuperAdmin
                ? null
                : await _tenants.GetAuthorizedTenantIdsAsync(User);

            var result = await _importService.ImportEmployeesAsync(
                memoryStream, effectiveTenantId, allowedTenantIds);

            _logger.LogInformation("Import result: {TotalRows} total, {Imported} imported, {Errors} errors (tenantId={TenantId})",
                result.TotalRows, result.Imported, result.Errors.Count, effectiveTenantId);

            if (!result.IsValid)
            {
                _logger.LogWarning("Import validation failed: {Errors}", string.Join("; ", result.Errors));
                return Ok(result);
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing employee CSV: {FileName}, Exception: {Exception}",
                file.FileName, ex.ToString());
            return Ok(new ImportResult
            {
                Errors = [$"Import error: {ex.GetType().Name}: {ex.Message}"],
                IsValid = false
            });
        }
    }

    /// <summary>Dry-run validation of shift CSV import for a schedule.</summary>
    [HttpPost("validate/schedule/{scheduleId}")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<ImportResult>> ValidateShifts(int scheduleId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ImportResult { Errors = ["CSV file is required."], IsValid = false });

        try
        {
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            memoryStream.Position = 0;
            var result = await _importService.ValidateScheduleImportAsync(scheduleId, memoryStream);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating shift CSV for schedule {ScheduleId}", scheduleId);
            return Ok(new ImportResult
            {
                Errors = [$"Validation error: {ex.Message}"],
                IsValid = false
            });
        }
    }

    /// <summary>Bulk assign shifts from CSV for a schedule.</summary>
    [HttpPost("schedule/{scheduleId}")]
    [Authorize(Policy = "RequireScheduleWrite")]
    public async Task<ActionResult<ImportResult>> ImportShifts(int scheduleId, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ImportResult { Errors = ["CSV file is required."], IsValid = false });

        _logger.LogInformation("Starting shift bulk import for schedule {ScheduleId}: {FileName}", scheduleId, file.FileName);
        try
        {
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            memoryStream.Position = 0;
            var result = await _importService.ImportShiftsAsync(scheduleId, memoryStream);

            if (!result.IsValid)
                return Ok(result);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing shift CSV for schedule {ScheduleId}", scheduleId);
            return Ok(new ImportResult
            {
                Errors = [$"Import error: {ex.Message}"],
                IsValid = false
            });
        }
    }
}
