using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;

namespace OnCallApi.Services;

/// <summary>
/// Writes settled code-call incidents out to the <c>incident-archive</c> blob container,
/// month by month, and leaves every row in SQL exactly where it was.
///
/// <para><b>Why this copies rather than moves.</b> <see cref="AuditArchiveService"/> deletes
/// rows once they are safely in blob storage, because audit rows are high-volume and almost
/// never read. Incident records are the opposite on both counts: a hospital produces a handful
/// a day, and the command center history is the thing somebody actually opens during a review.
/// Evicting them from SQL to satisfy a retention policy would make the seven-year record
/// unreadable in the product that is required to keep it. So this is an export, not an
/// eviction — the durability of a second, independent copy without the cost of losing the
/// first.</para>
///
/// <para><b>What it is defending against.</b> The database backups cover hardware and regional
/// failure. They do not help against a logical loss that goes unnoticed — a bad migration, a
/// cascade nobody predicted, an operator clearing a tenant — because the damage is faithfully
/// backed up along with everything else. A flat file per month, written once and never
/// rewritten after that month closes, survives all of those.</para>
///
/// <para>Work is bounded by only rewriting months that can still change. A month older than
/// the reconsolidation window is written once and then skipped forever, so a pass costs the
/// same whether the system is a week old or seven years old.</para>
/// </summary>
public class IncidentArchiveService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Long enough that startup, schema creation and seeding are done.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(7);

    /// <summary>
    /// How many recent months are rewritten on every pass.
    ///
    /// An incident is not finished when it is resolved: the debrief log is append-only and
    /// entries arrive days later, after the review meeting. Two months is comfortably past
    /// that, and a month older than this is treated as closed and written only if it is
    /// missing.
    /// </summary>
    private const int ReconsolidateMonths = 2;

    private const string DefaultContainerName = "incident-archive";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<IncidentArchiveService> _logger;

    public IncidentArchiveService(
        IServiceScopeFactory scopeFactory,
        IConfiguration config,
        ILogger<IncidentArchiveService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Hipaa:IncidentArchive:Enabled", true))
        {
            _logger.LogInformation(
                "Incident archiving is disabled (Hipaa:IncidentArchive:Enabled). "
                + "Code-call history will exist only in the database and its backups.");
            return;
        }

        var storageEndpoint = _config.GetValue<string>("Storage:ConnectionString");
        if (string.IsNullOrWhiteSpace(storageEndpoint))
        {
            // Nothing is deleted either way, so this is a warning rather than the error its
            // audit counterpart raises: the records are still in SQL, just not duplicated.
            _logger.LogWarning(
                "Incident archiving is enabled but Storage:ConnectionString is not set. "
                + "Code-call history will not be exported.");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExportAsync(storageEndpoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Incident archive pass failed; will retry on the next pass");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The blob holding one calendar month of incidents.</summary>
    internal static string BuildBlobName(int year, int month) =>
        string.Create(CultureInfo.InvariantCulture, $"{year:D4}/{month:D2}/incidents-{year:D4}-{month:D2}.jsonl");

    /// <summary>
    /// Whether a month is still open to change, and so must be rewritten on every pass.
    /// </summary>
    internal static bool IsReconsolidating(DateTime nowUtc, int year, int month)
    {
        var monthsAgo = ((nowUtc.Year - year) * 12) + (nowUtc.Month - month);
        return monthsAgo <= ReconsolidateMonths;
    }

    /// <summary>
    /// One JSON object per line (NDJSON), matching the audit archive, so both containers can
    /// be read by the same tooling a record at a time.
    /// </summary>
    internal static string SerializeBatch(IEnumerable<object> incidents)
    {
        var builder = new StringBuilder();
        foreach (var incident in incidents)
        {
            builder.Append(JsonSerializer.Serialize(incident)).Append('\n');
        }
        return builder.ToString();
    }

    private async Task ExportAsync(string storageEndpoint, CancellationToken ct)
    {
        var containerName = _config.GetValue("Hipaa:IncidentArchive:ContainerName", DefaultContainerName)
            ?? DefaultContainerName;

        var container = CreateContainerClient(storageEndpoint, containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Which months have anything in them at all. Cheap, and it means an empty deployment
        // does no work rather than probing blob storage for months that never existed.
        var months = await db.PhoneTreeEvents
            .Select(e => new { e.StartedAt.Year, e.StartedAt.Month })
            .Distinct()
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var written = 0;

        foreach (var m in months.OrderBy(x => x.Year).ThenBy(x => x.Month))
        {
            if (ct.IsCancellationRequested) return;

            var blob = container.GetBlobClient(BuildBlobName(m.Year, m.Month));

            // A closed month that is already written is finished. This is what keeps the cost
            // of a pass flat as history accumulates.
            if (!IsReconsolidating(now, m.Year, m.Month) && await blob.ExistsAsync(ct))
            {
                continue;
            }

            var from = new DateTime(m.Year, m.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = from.AddMonths(1);

            var incidents = await db.PhoneTreeEvents
                .Include(e => e.PhoneTree)
                .Include(e => e.Participants)
                .Include(e => e.DispatchSteps)
                .Include(e => e.DebriefLog)
                .Where(e => e.StartedAt >= from && e.StartedAt < to)
                .OrderBy(e => e.Id)
                .AsNoTracking()
                .ToListAsync(ct);

            if (incidents.Count == 0) continue;

            // Flattened deliberately: an archive that needs the application's object graph to
            // be read is an archive that stops being readable the day the application changes.
            var records = incidents.Select(e => (object)new
            {
                e.Id,
                Code = e.PhoneTree?.Name,
                CodeType = e.PhoneTree?.TreeType,
                e.StartedAt,
                e.EndedAt,
                e.AcknowledgedAt,
                e.Status,
                e.Location,
                e.LocationZone,
                e.ResponseTimeSeconds,
                e.InitiatedById,
                e.InitiatedByName,
                e.InitiatedByEmail,
                e.RequestedByName,
                e.NotifiedByName,
                e.Outcome,
                e.Notes,
                e.ExternalIncidentId,
                LegacyDebriefNote = e.DebriefNotes,
                DebriefLog = e.DebriefLog
                    .OrderBy(n => n.CreatedAt)
                    .Select(n => new { n.Id, n.Note, n.AuthorName, n.CreatedAt }),
                DispatchSteps = e.DispatchSteps
                    .OrderBy(s => s.StartedAt)
                    .Select(s => new { s.Id, s.StepKey, s.Status, s.StartedAt, s.CompletedAt, s.Detail }),
                Participants = e.Participants
                    .Select(p => new { p.Id, p.EmployeeId, p.Role, p.RespondedAt, p.AcknowledgedAt }),
            });

            var payload = SerializeBatch(records);

            // overwrite: true with a deterministic name, exactly as the audit archive does — a
            // pass interrupted halfway rewrites the same blob next time rather than leaving a
            // duplicate or a gap.
            await blob.UploadAsync(
                new BinaryData(Encoding.UTF8.GetBytes(payload)), overwrite: true, cancellationToken: ct);

            written++;
            _logger.LogInformation(
                "Archived {Count} incident(s) for {Year:D4}-{Month:D2} to {BlobName}",
                incidents.Count, m.Year, m.Month, blob.Name);
        }

        if (written > 0)
        {
            _logger.LogInformation(
                "Incident archive pass complete: {Months} month file(s) written. Nothing was deleted from SQL.",
                written);
        }
    }

    private static BlobContainerClient CreateContainerClient(string storageEndpoint, string containerName)
    {
        var service = storageEndpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new BlobServiceClient(new Uri(storageEndpoint), new DefaultAzureCredential())
            : new BlobServiceClient(storageEndpoint);

        return service.GetBlobContainerClient(containerName);
    }
}
