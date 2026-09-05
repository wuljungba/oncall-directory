using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Moves audit rows out of SQL once they are older than the hot window, into the
/// <c>audit-archive</c> blob container, and only then deletes them from the database.
///
/// This exists because <c>Hipaa:AuditLogRetentionDays</c> was configuration that nothing
/// read: rows accumulated forever, and the six-year retention the settings claimed was
/// enforced by nobody in either direction. The table is also the fastest-growing thing in
/// the schema — one row per PHI-touching request — so it is what eventually decides the
/// database tier.
///
/// The ordering is the safety property and is not negotiable: a batch is written to blob
/// storage and confirmed before a single row is deleted. A failed upload leaves the rows
/// in SQL and is retried on the next pass. Because blob names are derived from the id range
/// they contain, a retry after a crash rewrites the identical blob rather than duplicating
/// or skipping one.
///
/// Disabled by default. It deletes audit records, so switching it on is a deliberate
/// per-environment decision, not something a deployment does on someone's behalf.
/// </summary>
public class AuditArchiveService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Long enough that startup, schema creation and seeding are done.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Rows read, uploaded and deleted as one unit. A cycle keeps taking batches until the
    /// backlog is clear, so this bounds memory rather than throughput.
    /// </summary>
    private const int DefaultBatchSize = 5000;

    /// <summary>How long audit rows stay queryable in SQL before being archived.</summary>
    private const int DefaultHotDays = 90;

    private const string DefaultContainerName = "audit-archive";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<AuditArchiveService> _logger;

    public AuditArchiveService(
        IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<AuditArchiveService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Hipaa:AuditArchive:Enabled", false))
        {
            _logger.LogInformation(
                "Audit archiving is disabled (Hipaa:AuditArchive:Enabled). Audit rows will accumulate in SQL indefinitely.");
            return;
        }

        var storageEndpoint = _config.GetValue<string>("Storage:ConnectionString");
        if (string.IsNullOrWhiteSpace(storageEndpoint))
        {
            // Enabled but unable to archive is a misconfiguration, not a quiet no-op: without
            // somewhere to put the rows the only alternative to saying so is deleting them.
            _logger.LogError(
                "Audit archiving is enabled but Storage:ConnectionString is not set. No rows will be archived or deleted.");
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
                await ArchiveAsync(storageEndpoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Housekeeping must never take the application down. Nothing has been deleted
                // unless its blob was written first, so a failed pass is safe to retry.
                _logger.LogError(ex, "Audit archive pass failed; will retry on the next pass");
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

    /// <summary>
    /// The instant before which audit rows are eligible to leave SQL.
    /// </summary>
    internal static DateTime ResolveCutoff(DateTime nowUtc, int hotDays) =>
        nowUtc.AddDays(-Math.Max(hotDays, 1));

    /// <summary>
    /// Deterministic blob path for one batch, derived from the id range it holds.
    ///
    /// Determinism is what makes a retry safe: a pass that uploaded a batch and then died
    /// before deleting it will, on the next pass, select the same rows and rewrite the same
    /// blob. Foldered by the newest timestamp in the batch so an auditor asking for a period
    /// can narrow by prefix instead of listing the container.
    /// </summary>
    internal static string BuildBlobName(DateTime newestTimestampUtc, long firstId, long lastId) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{newestTimestampUtc:yyyy}/{newestTimestampUtc:MM}/audit-{firstId:D19}-{lastId:D19}.jsonl");

    /// <summary>
    /// One JSON object per line (NDJSON), so a multi-gigabyte archive can be read a record
    /// at a time and appended to without reparsing.
    /// </summary>
    internal static string SerializeBatch(IEnumerable<AuditLog> logs)
    {
        var builder = new StringBuilder();
        foreach (var log in logs)
        {
            builder.Append(JsonSerializer.Serialize(log)).Append('\n');
        }
        return builder.ToString();
    }

    private async Task ArchiveAsync(string storageEndpoint, CancellationToken ct)
    {
        var hotDays = _config.GetValue("Hipaa:AuditArchive:HotDays", DefaultHotDays);
        var batchSize = Math.Max(_config.GetValue("Hipaa:AuditArchive:BatchSize", DefaultBatchSize), 1);
        var containerName = _config.GetValue("Hipaa:AuditArchive:ContainerName", DefaultContainerName)
            ?? DefaultContainerName;

        var cutoff = ResolveCutoff(DateTime.UtcNow, hotDays);
        var container = CreateContainerClient(storageEndpoint, containerName);

        var archivedTotal = 0;

        while (!ct.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Oldest first, so a backlog drains in the order it accumulated and the id range
            // in each blob name stays contiguous.
            var batch = await db.AuditLogs
                .Where(a => a.Timestamp < cutoff)
                .OrderBy(a => a.Id)
                .Take(batchSize)
                .AsNoTracking()
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            var firstId = batch[0].Id;
            var lastId = batch[^1].Id;
            var newest = batch.Max(a => a.Timestamp);
            var blobName = BuildBlobName(newest, firstId, lastId);

            // Upload first, and let a failure throw. Reaching the delete below is the only
            // evidence that these rows exist somewhere else.
            var payload = SerializeBatch(batch);
            var blob = container.GetBlobClient(blobName);
            await blob.UploadAsync(
                new BinaryData(Encoding.UTF8.GetBytes(payload)), overwrite: true, cancellationToken: ct);

            // Bounded by the same predicate that selected the batch. Every row matching it is
            // one we just archived: the query took the `batchSize` lowest ids below the
            // cutoff, so anything else below the cutoff has an id above lastId.
            var deleted = await db.AuditLogs
                .Where(a => a.Timestamp < cutoff && a.Id <= lastId)
                .ExecuteDeleteAsync(ct);

            archivedTotal += batch.Count;
            _logger.LogInformation(
                "Archived {Count} audit row(s) (ids {FirstId}-{LastId}) to {BlobName}; {Deleted} row(s) removed from SQL",
                batch.Count, firstId, lastId, blobName, deleted);

            if (batch.Count < batchSize) break;
        }

        if (archivedTotal > 0)
        {
            _logger.LogInformation(
                "Audit archive pass complete: {Total} row(s) older than {Cutoff:u} moved to blob storage",
                archivedTotal, cutoff);
        }
    }

    /// <summary>
    /// Managed identity against a blob endpoint URL is what the deployed app is set up for —
    /// Bicep hands it the endpoint and a Storage Blob Data Contributor assignment. A literal
    /// connection string is still accepted, because the setting is named for one.
    /// </summary>
    private static BlobContainerClient CreateContainerClient(string storageEndpoint, string containerName)
    {
        var service = storageEndpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new BlobServiceClient(new Uri(storageEndpoint), new DefaultAzureCredential())
            : new BlobServiceClient(storageEndpoint);

        return service.GetBlobContainerClient(containerName);
    }
}
