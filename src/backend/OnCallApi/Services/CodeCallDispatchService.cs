using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OnCallApi.Data;
using OnCallApi.Hubs;
using OnCallApi.Models;

namespace OnCallApi.Services;

/// <summary>
/// Stub implementation that simulates the dispatch pipeline with timed delays.
/// In production, replace with real calls to InformaCast, Vocera, and Cisco CUCM AXL APIs.
/// </summary>
public class CodeCallDispatchService : ICodeCallDispatchService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<OnCallNotificationHub> _hub;
    private readonly ILogger<CodeCallDispatchService> _logger;

    public CodeCallDispatchService(
        IServiceScopeFactory scopeFactory,
        IHubContext<OnCallNotificationHub> hub,
        ILogger<CodeCallDispatchService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    public async Task DispatchIncidentAsync(PhoneTreeEvent evt, string codeType)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Step 1: CUCM AXL pre-flight check (simulated 500ms)
                await Task.Delay(500);
                await RecordStepAndNotify(evt.Id, "cucm_check", "completed",
                    $"CUCM AXL pre-flight check: paging devices at {evt.Location ?? "location"} registered");

                // Step 2: InformaCast broadcast (simulated 800ms)
                await Task.Delay(800);
                await RecordStepAndNotify(evt.Id, "informacast", "completed",
                    $"InformaCast broadcast triggered: {codeType} @ {evt.Location ?? "location"} (overhead speakers + desk phones)");

                // Step 3: Vocera alert (simulated 1200ms)
                await Task.Delay(1200);
                await RecordStepAndNotify(evt.Id, "vocera", "completed",
                    $"Vocera alert sent to responder group: {codeType} (badges + Smartphone app)");

                // Step 4: Auto-acknowledgment (simulated 2000ms)
                await Task.Delay(2000);
                await RecordStepAndNotify(evt.Id, "acknowledged", "completed",
                    "First responder acknowledged via Vocera (charge nurse)");

                // Auto-resolve after all steps complete
                using (var scope = _scopeFactory.CreateScope())
                {
                    var service = scope.ServiceProvider.GetRequiredService<IPhoneTreeEventService>();
                    await service.AcknowledgeEventAsync(evt.Id);
                }

                _logger.LogInformation("Dispatch pipeline complete for event {EventId}", evt.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatch pipeline failed for event {EventId}", evt.Id);

                await RecordStepAndNotify(evt.Id, "sip_fallback", "failed",
                    $"All dispatch channels failed: {ex.Message}");
            }
        });
    }

    public async Task<bool> PreflightCheckDevicesAsync(string location)
    {
        // Stub: always returns true
        await Task.Delay(200);
        return true;
    }

    private async Task RecordStepAndNotify(int eventId, string stepKey, string status, string detail)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var step = new DispatchStep
            {
                PhoneTreeEventId = eventId,
                StepKey = stepKey,
                Status = status,
                CompletedAt = DateTime.UtcNow,
                Detail = detail,
            };
            db.DispatchSteps.Add(step);
            await db.SaveChangesAsync();
        }

        await _hub.Clients.All.SendAsync("DispatchStepCompleted", new
        {
            eventId,
            stepKey,
            status,
            detail,
            completedAt = DateTime.UtcNow,
        });
    }
}
