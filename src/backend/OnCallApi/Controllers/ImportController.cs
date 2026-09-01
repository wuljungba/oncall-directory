using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;
using OnCallApi.Services;
using OnCallApi.Services.Import;

namespace OnCallApi.Controllers;

[ApiController]
[Route("api/import")]
public class ImportController : ControllerBase
{
    private readonly BulkImportService _importService;
    private readonly ImportJobService _jobs;
    private readonly AppDbContext _db;
    private readonly ITenantContextService _tenants;
    private readonly ILogger<ImportController> _logger;

    public ImportController(
        BulkImportService importService,
        ImportJobService jobs,
        AppDbContext db,
        ITenantContextService tenants,
        ILogger<ImportController> logger)
    {
        _importService = importService;
        _jobs = jobs;
        _db = db;
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
            var isSuperAdminCheck = _tenants.IsSuperAdmin(User);
            var validateScope = isSuperAdminCheck ? null : await _tenants.GetAuthorizedTenantIdsAsync(User);
            var result = await _importService.ValidateEmployeesAsync(memoryStream, null, validateScope);
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

    // ── Staged multi-sheet import ──
    //
    // The single-call endpoints above stay: they are the right shape for a clean CSV, and
    // are what the existing tests and callers use. These add the flow a real workbook
    // needs -- read every sheet, correct the mapping, see what will happen, then commit.

    /// <summary>Reads an upload into a staged job and returns what was found.</summary>
    [HttpPost("jobs")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public async Task<ActionResult<ImportJobPreview>> CreateJob(IFormFile file, [FromQuery] int? tenantId = null)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "A CSV or Excel file is required." });

        var (effectiveTenantId, allowedTenantIds) = await ResolveScopeAsync(tenantId);

        using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        var (job, error) = await _jobs.CreateAsync(
            memoryStream, file.FileName, effectiveTenantId, CurrentUserName(), CurrentPrincipalId());

        if (error != null || job == null) return BadRequest(new { error });

        _logger.LogInformation(
            "Import job {JobId} created from {FileName} ({Sheets} sheets, {Rows} rows)",
            job.Id, file.FileName, job.SheetCount, job.TotalRows);

        return Ok(await _jobs.BuildPreviewAsync(job, allowedTenantIds));
    }

    /// <summary>What a commit would do, under the mapping as it currently stands.</summary>
    [HttpGet("jobs/{id}")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public async Task<ActionResult<ImportJobPreview>> GetJob(int id)
    {
        var (job, allowedTenantIds, failure) = await LoadJobAsync(id);
        if (failure != null) return failure;

        return Ok(await _jobs.BuildPreviewAsync(job!, allowedTenantIds));
    }

    /// <summary>Sets which column means what, for one sheet or for all of them.</summary>
    [HttpPut("jobs/{id}/mapping")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public async Task<ActionResult<ImportJobPreview>> UpdateMapping(int id, [FromBody] UpdateImportMappingRequest request)
    {
        var (job, allowedTenantIds, failure) = await LoadJobAsync(id);
        if (failure != null) return failure;

        var updated = await _jobs.UpdateMappingAsync(
            job!, request.SheetName ?? string.Empty,
            request.Columns ?? new Dictionary<string, string>(),
            request.ApplyToAllSheets, request.ExcludedSheets);

        if (!updated)
            return Conflict(new { error = "This import has already been committed and cannot be changed." });

        return Ok(await _jobs.BuildPreviewAsync(job!, allowedTenantIds));
    }

    /// <summary>Chooses what happens to one row that matched somebody already in the directory.</summary>
    [HttpPut("jobs/{id}/rows/{rowId}")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public async Task<ActionResult<ImportJobPreview>> SetRowResolution(
        int id, int rowId, [FromBody] SetImportRowResolutionRequest request)
    {
        var (job, allowedTenantIds, failure) = await LoadJobAsync(id);
        if (failure != null) return failure;

        var updated = await _jobs.SetResolutionAsync(job!, rowId, request.Resolution ?? string.Empty);
        if (!updated)
            return BadRequest(new { error = "That row or resolution is not valid for this import." });

        return Ok(await _jobs.BuildPreviewAsync(job!, allowedTenantIds));
    }

    /// <summary>Writes the included rows. All of them, or none.</summary>
    [HttpPost("jobs/{id}/commit")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public async Task<ActionResult<ImportResult>> CommitJob(int id)
    {
        var (job, allowedTenantIds, failure) = await LoadJobAsync(id);
        if (failure != null) return failure;

        var (result, error) = await _jobs.CommitAsync(job!, allowedTenantIds);
        if (error != null) return BadRequest(new { error });

        _logger.LogInformation("Import job {JobId} committed: {Imported} row(s)", id, result.Imported);
        return Ok(result);
    }

    /// <summary>The rows that could not be imported, as uploaded, with a reason column.</summary>
    [HttpGet("jobs/{id}/errors")]
    [Authorize(Policy = "RequireDirectoryWrite")]
    public async Task<IActionResult> DownloadErrors(int id)
    {
        var (job, _, failure) = await LoadJobAsync(id);
        if (failure != null) return failure;

        var csv = await _jobs.BuildErrorReportAsync(job!);
        return File(csv, "text/csv", $"import-{id}-problems.csv");
    }

    // ── Shared scoping ──

    /// <summary>
    /// Which subscription rows are created in, and which existing records this caller may
    /// touch. A super admin may name a subscription; anyone else is forced to their own
    /// regardless of what the query string says.
    /// </summary>
    private async Task<(int? EffectiveTenantId, List<int>? AllowedTenantIds)> ResolveScopeAsync(int? requestedTenantId)
    {
        var isSuperAdmin = _tenants.IsSuperAdmin(User);
        if (isSuperAdmin) return (requestedTenantId, null);

        var authorized = await _tenants.GetAuthorizedTenantIdsAsync(User);
        return (authorized.FirstOrDefault(), authorized);
    }

    /// <summary>
    /// Loads a job the caller is allowed to see.
    ///
    /// A staged job holds directory data belonging to a subscription, so it is scoped the
    /// same way the directory is. A 404 rather than a 403 for someone else's job: whether
    /// a given job id exists is not their business either.
    /// </summary>
    private async Task<(ImportJob? Job, List<int>? AllowedTenantIds, ActionResult? Failure)> LoadJobAsync(int id)
    {
        var (_, allowedTenantIds) = await ResolveScopeAsync(null);

        var job = await _db.ImportJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return (null, null, NotFound(new { error = "Import not found." }));

        if (allowedTenantIds != null
            && !(job.TenantId.HasValue && allowedTenantIds.Contains(job.TenantId.Value)))
        {
            return (null, null, NotFound(new { error = "Import not found." }));
        }

        return (job, allowedTenantIds, null);
    }

    private string? CurrentUserName() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
        ?? User.FindFirst("name")?.Value
        ?? User.FindFirst("preferred_username")?.Value;

    private string? CurrentPrincipalId() =>
        User.FindFirst("oid")?.Value
        ?? User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
        ?? User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
        ?? User.FindFirst("preferred_username")?.Value;
}

/// <summary>Body of a mapping change. Every field is caller-supplied and untrusted.</summary>
public record UpdateImportMappingRequest(
    string? SheetName,
    Dictionary<string, string>? Columns,
    bool ApplyToAllSheets = false,
    List<string>? ExcludedSheets = null);

/// <summary>Body of a per-row decision: create, merge or skip.</summary>
public record SetImportRowResolutionRequest(string? Resolution);
