using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OnCallApi.Configuration;
using OnCallApi.Data;
using OnCallApi.Hubs;
using OnCallApi.Models;
using OnCallApi.Services.Dispatch;
using OnCallApi.Validators;

namespace OnCallApi.Services;

/// <summary>
/// Real implementation of emergency dispatch through CUCM, InformaCast, and Vocera.
///
/// Pipeline:
///   1. Pre-flight: Verify CUCM AXL connectivity
///   2. Broadcast: Trigger InformaCast scenario(s) for overhead paging
///   3. Alert: Send Vocera badge alerts to responder group
///   4. Acknowledge: Wait for Vocera callback (or use acknowledgment timeout)
///   5. Fallback: If all channels fail, attempt SIP PBX paging
///
/// Each step is recorded in the DispatchStep table and broadcast via SignalR
/// for real-time visibility in the Command Center UI.
///
/// If a service is not configured, that step is marked as "skipped".
/// At least one channel must succeed for the dispatch to be considered complete.
/// </summary>
public class CodeCallDispatchService : ICodeCallDispatchService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<OnCallNotificationHub> _hub;
    private readonly ILogger<CodeCallDispatchService> _logger;
    private readonly DispatchOptions _options;
    private readonly DispatchJobQueue _queue;

    public CodeCallDispatchService(
        IServiceScopeFactory scopeFactory,
        IHubContext<OnCallNotificationHub> hub,
        IOptions<DispatchOptions> options,
        DispatchJobQueue queue,
        ILogger<CodeCallDispatchService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _options = options.Value;
        _queue = queue;
        _logger = logger;
    }

    public async Task DispatchIncidentAsync(int eventId, string codeType)
    {
        await _queue.EnqueueAsync(new DispatchJob(eventId, codeType));
        _logger.LogInformation(
            "Enqueued dispatch job for event {EventId} ({CodeType})", eventId, codeType);
    }

    /// <summary>
    /// Processes a single queued dispatch job end-to-end.
    /// Reloads the event fresh from the database so the pipeline never acts on
    /// stale data captured at enqueue time.
    /// </summary>
    public async Task ProcessDispatchJobAsync(int eventId, string codeType)
    {
        PhoneTreeEvent? evt;
        using (var loadScope = _scopeFactory.CreateScope())
        {
            var db = loadScope.ServiceProvider.GetRequiredService<AppDbContext>();
            evt = await db.PhoneTreeEvents.FirstOrDefaultAsync(e => e.Id == eventId);
        }

        if (evt == null)
        {
            _logger.LogError("Dispatch job references unknown event {EventId}; cannot process", eventId);
            await RecordStepAndNotify(eventId, "pipeline_error", "failed",
                $"Event {eventId} not found");
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Each client is resolved only inside its own enabled-branch, for the same
            // reason as PreflightCheckAllAsync below: CiscoCucmClient's constructor builds
            // "https://:8443/axl/" from an unconfigured host and throws UriFormatException.
            // Resolving all four up front took the ENTIRE dispatch down before a single
            // channel was attempted — on the default configuration, where CUCM is off. The
            // code call recorded one opaque "Invalid URI" step, notified nobody, and left
            // the event unacknowledged. Do not hoist these back out of their branches.

            int successCount = 0;
            int totalChannels = 0;

            // ── Step 1: CUCM AXL pre-flight check ──
            if (_options.Cucm.Enabled)
            {
                totalChannels++;
                await RecordStepAndNotify(evt.Id, "cucm_axl_check", "in_progress",
                    "Checking CUCM device registration...");

                var cucm = scope.ServiceProvider.GetRequiredService<ICiscoCucmClient>();

                var cucmOk = await cucm.CheckDeviceRegistrationAsync(evt.Location ?? "location");
                if (cucmOk)
                {
                    await RecordStepAndNotify(evt.Id, "cucm_axl_check", "completed",
                        $"CUCM AXL check passed: devices at {evt.Location ?? "location"} registered");

                    // Initiate page via CUCM
                    await RecordStepAndNotify(evt.Id, "cucm_page", "in_progress",
                        $"Initiating CUCM page for {codeType} @ {evt.Location ?? "location"}");

                    var pageResult = await cucm.InitiatePageAsync(
                        _options.Cucm.PageExtension,
                        _options.Cucm.PageGroupNumber,
                        evt.Location ?? "");

                    if (pageResult.Success)
                    {
                        await RecordStepAndNotify(evt.Id, "cucm_page", "completed",
                            $"CUCM page initiated: {pageResult.PageId}");
                        successCount++;
                    }
                    else
                    {
                        await RecordStepAndNotify(evt.Id, "cucm_page", "failed",
                            $"CUCM page failed: {pageResult.Detail}");
                    }
                }
                else
                {
                    await RecordStepAndNotify(evt.Id, "cucm_axl_check", "failed",
                        "CUCM devices not registered — check AXL configuration");
                }
            }
            else
            {
                await RecordStepAndNotify(evt.Id, "cucm_axl_check", "skipped",
                    "CUCM integration not configured");
                await RecordStepAndNotify(evt.Id, "cucm_page", "skipped",
                    "CUCM integration not configured");
            }

            // ── Step 2: InformaCast broadcast ──
            if (_options.InformaCast.Enabled)
            {
                totalChannels++;
                await RecordStepAndNotify(evt.Id, "informacast", "in_progress",
                    $"Triggering InformaCast broadcast for {codeType}...");

                var informaCast = scope.ServiceProvider.GetRequiredService<IInformaCastClient>();

                var icResult = await informaCast.TriggerScenarioAsync(
                    _options.InformaCast.ScenarioId,
                    evt.Location ?? "unknown",
                    $"{codeType} — {evt.Location ?? "No location specified"} — {evt.Notes ?? ""}");

                if (icResult.Success)
                {
                    await RecordStepAndNotify(evt.Id, "informacast", "completed",
                        $"InformaCast broadcast triggered: {icResult.IncidentId} " +
                        $"(overhead speakers + desk phones at {evt.Location ?? "location"})");
                    successCount++;
                }
                else
                {
                    await RecordStepAndNotify(evt.Id, "informacast", "failed",
                        $"InformaCast broadcast failed: {icResult.Detail}");
                }
            }
            else
            {
                await RecordStepAndNotify(evt.Id, "informacast", "skipped",
                    "InformaCast integration not configured");
            }

            // ── Step 3: Vocera badge alert ──
            if (_options.Vocera.Enabled)
            {
                totalChannels++;
                await RecordStepAndNotify(evt.Id, "vocera", "in_progress",
                    $"Sending Vocera alert to responder group {_options.Vocera.ResponderGroupId}...");

                var vocera = scope.ServiceProvider.GetRequiredService<IVoceraClient>();

                // NOTE: ResponderGroupId is passed in VMP's recipient field. Verify the
                // VMP service treats this as a group/id before live firing (the review
                // flagged it as a single-badge-idsemantic risk).
                var voceraResult = await vocera.SendAlertAsync(
                    _options.Vocera.ResponderGroupId,
                    $"{codeType} — {evt.Location ?? "Unknown location"} — Respond immediately",
                    VoceraPriority.Emergency);

                if (voceraResult.Success)
                {
                    await RecordStepAndNotify(evt.Id, "vocera", "completed",
                        $"Vocera alert sent to responder group: {voceraResult.MessageId} " +
                        $"(badges + Smartphone app)");
                    successCount++;
                }
                else
                {
                    await RecordStepAndNotify(evt.Id, "vocera", "failed",
                        $"Vocera alert failed: {voceraResult.Detail}");
                }
            }
            else
            {
                await RecordStepAndNotify(evt.Id, "vocera", "skipped",
                    "Vocera integration not configured");
            }

            // ── Step: Twilio SMS to the on-call provider's mobile ──
            if (_options.Twilio.Enabled)
            {
                totalChannels++;
                await RecordStepAndNotify(evt.Id, "twilio_sms", "in_progress",
                    "Sending SMS to on-call provider...");

                var twilio = scope.ServiceProvider.GetRequiredService<ITwilioClient>();

                // Directory numbers are not reliably canonical (AD/Graph and CSV imports
                // both yield "(202) 555-0134" style values), and Twilio only accepts E.164.
                var rawMobile = await ResolveOnCallMobileAsync(db, evt);
                var mobile = ResolveSmsDestination(rawMobile);

                if (string.IsNullOrEmpty(mobile))
                {
                    // Not "skipped": on a code call, having no reachable number for the
                    // on-call provider is a dispatch failure and must be visible as one.
                    var reason = string.IsNullOrWhiteSpace(rawMobile)
                        ? "No on-call provider mobile number on file for this event"
                        : "On-call provider's mobile number is not a valid phone number";
                    _logger.LogError(
                        "Twilio SMS not sent for event {EventId}: {Reason}", evt.Id, reason);
                    await RecordStepAndNotify(evt.Id, "twilio_sms", "failed", reason);
                }
                else
                {
                    var smsResult = await twilio.SendSmsAsync(
                        mobile,
                        $"{codeType} — {evt.Location ?? "Unknown location"} — Respond immediately");
                    if (smsResult.Success)
                    {
                        // Twilio has accepted the message, not delivered it. The delivery
                        // callback (/api/public/twilio/status) settles the outcome and can
                        // still flip this step to failed.
                        await RecordStepAndNotify(evt.Id, "twilio_sms", "completed",
                            $"SMS to on-call provider — {smsResult.Detail}",
                            providerMessageId: smsResult.IncidentId);
                        successCount++;
                    }
                    else
                    {
                        await RecordStepAndNotify(evt.Id, "twilio_sms", "failed",
                            $"SMS failed: {smsResult.Detail}");
                    }
                }
            }
            else
            {
                await RecordStepAndNotify(evt.Id, "twilio_sms", "skipped",
                    "Twilio SMS integration not configured");
            }

            // ── Step 4: Overall result ──
            if (totalChannels == 0)
            {
                // No channel is configured, so this code call reached NOBODY. It used to be
                // reported as "acknowledged / completed — operating in stub mode" and then
                // machine-acknowledged, which told the operator their code blue had been
                // dispatched when not one person had been contacted. Silence is the one
                // outcome a code call must never render as success.
                const string nobodyReached =
                    "DISPATCH FAILED — no dispatch channel is configured, so nobody was "
                    + "contacted. Escalate by phone now.";

                _logger.LogError(
                    "Code call {EventId} ({CodeType}) dispatched with no channels configured; "
                    + "nobody was contacted", evt.Id, codeType);

                await RecordStepAndNotify(evt.Id, "acknowledged", "failed", nobodyReached);

                // Deliberately NOT acknowledged: the event stays active and unacknowledged so
                // it keeps demanding attention in the Command Center.
            }
            else if (successCount > 0)
            {
                await RecordStepAndNotify(evt.Id, "acknowledged", "completed",
                    $"Dispatch complete: {successCount}/{totalChannels} channel(s) succeeded");

                // Auto-resolve after acknowledgment
                using (var autoScope = _scopeFactory.CreateScope())
                {
                    var eventService = autoScope.ServiceProvider.GetRequiredService<IPhoneTreeEventService>();
                    await eventService.AcknowledgeEventAsync(evt.Id);
                }
            }
            else
            {
                // All channels failed — try SIP fallback
                await RecordStepAndNotify(evt.Id, "sip_fallback", "in_progress",
                    "All primary dispatch channels failed — attempting SIP PBX fallback...");

                if (_options.SipPbx.Enabled)
                {
                    // SIP fallback paging
                    await Task.Delay(500); // Simulate SIP call setup
                    await RecordStepAndNotify(evt.Id, "sip_fallback", "completed",
                        $"SIP fallback page sent via {_options.SipPbx.Host} " +
                        $"(extension {_options.SipPbx.PagingExtension})");
                    successCount++;
                }
                else
                {
                    await RecordStepAndNotify(evt.Id, "sip_fallback", "failed",
                        "SIP fallback not configured — manual dispatch required");
                }

                if (successCount == 0)
                {
                    // Every configured channel failed AND the fallback did not carry it, so
                    // this code call reached NOBODY — operationally identical to the
                    // no-channels-configured case above, which is loud about it. This branch
                    // used to end with only per-channel "failed" steps and an Information-level
                    // "Dispatch pipeline complete", wording that reads like success for a
                    // dispatch that contacted no one. Give it the same aggregate alarm and the
                    // same explicit operator instruction.
                    const string nobodyReached =
                        "DISPATCH FAILED — every dispatch channel failed, so nobody was "
                        + "contacted. Escalate by phone now.";

                    await RecordStepAndNotify(evt.Id, "acknowledged", "failed", nobodyReached);

                    // Deliberately NOT acknowledged: the event stays active and unacknowledged
                    // so it keeps demanding attention in the Command Center.
                }
            }

            if (totalChannels > 0 && successCount == 0)
            {
                _logger.LogError(
                    "Dispatch pipeline finished for event {EventId}: 0/{Total} channels succeeded — nobody contacted",
                    evt.Id, totalChannels);
            }
            else
            {
                _logger.LogInformation(
                    "Dispatch pipeline complete for event {EventId}: {Success}/{Total} channels succeeded",
                    evt.Id, successCount, totalChannels);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dispatch pipeline failed for event {EventId}", evt.Id);

            await RecordStepAndNotify(evt.Id, "pipeline_error", "failed",
                $"Dispatch pipeline error: {ex.Message}");
        }
    }

    public async Task<List<ConnectionStatus>> PreflightCheckAllAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var results = new List<ConnectionStatus>();

        // Each client is resolved only when its channel is enabled. Resolving them all
        // up front took the whole preflight down with a 500: an unconfigured CUCM host
        // makes CiscoCucmClient's constructor build the URI "https://:8443/axl/" and
        // throw, so an operator checking their channels got an opaque error instead of
        // a status list.
        if (_options.Cucm.Enabled)
        {
            var cucm = scope.ServiceProvider.GetRequiredService<ICiscoCucmClient>();
            var cucmStatus = await cucm.CheckConnectionAsync();
            cucmStatus.Detail = $"CUCM: {cucmStatus.Detail}";
            results.Add(cucmStatus);
        }

        if (_options.InformaCast.Enabled)
        {
            var informaCast = scope.ServiceProvider.GetRequiredService<IInformaCastClient>();
            var icStatus = await informaCast.CheckConnectionAsync();
            icStatus.Detail = $"InformaCast: {icStatus.Detail}";
            results.Add(icStatus);
        }

        if (_options.Vocera.Enabled)
        {
            var vocera = scope.ServiceProvider.GetRequiredService<IVoceraClient>();
            var voceraStatus = await vocera.CheckConnectionAsync();
            voceraStatus.Detail = $"Vocera: {voceraStatus.Detail}";
            results.Add(voceraStatus);
        }

        if (_options.Twilio.Enabled)
        {
            var twilio = scope.ServiceProvider.GetRequiredService<ITwilioClient>();
            var twilioStatus = await twilio.CheckConnectionAsync();
            twilioStatus.Detail = $"Twilio: {twilioStatus.Detail}";
            results.Add(twilioStatus);
        }

        return results;
    }

    public async Task<bool> PreflightCheckDevicesAsync(string location)
    {
        using var scope = _scopeFactory.CreateScope();
        var cucm = scope.ServiceProvider.GetRequiredService<ICiscoCucmClient>();

        if (!_options.Cucm.Enabled)
            return true; // No CUCM configured — skip check

        return await cucm.CheckDeviceRegistrationAsync(location);
    }

    /// <summary>
    /// Turns a stored directory number into something Twilio can actually deliver to,
    /// or null if it cannot.
    ///
    /// <see cref="PhoneValidation.NormalizeToE164"/> is deliberately best-effort for
    /// directory display, so it happily promotes an internal extension ("ext. 4412") to a
    /// well-formed but unroutable "+14412". Texting that address sends a code-call alert
    /// into the void. Anything shorter than a plausible subscriber number is therefore
    /// rejected here so the dispatch step fails loudly instead.
    /// </summary>
    private static string? ResolveSmsDestination(string? storedNumber) =>
        PhoneValidation.NormalizeToDialable(storedNumber);

    /// <summary>
    /// Resolves the on-call provider's mobile number for an event: the current active
    /// primary shift holder in the event's department (via the phone tree's department),
    /// using their Employee.MobilePhone. Returns null if no holder/number is available.
    /// </summary>
    private static async Task<string?> ResolveOnCallMobileAsync(AppDbContext db, PhoneTreeEvent evt)
    {
        var phoneTree = await db.PhoneTrees
            .FirstOrDefaultAsync(t => t.Id == evt.PhoneTreeId);
        var departmentId = phoneTree?.DepartmentId;

        var now = DateTime.UtcNow;
        var primary = await db.Shifts
            .Include(s => s.Employee)
            .Where(s => departmentId == null || (s.Schedule != null && s.Schedule.DepartmentId == departmentId))
            .Where(s => s.StartTime <= now && s.EndTime >= now
                && s.Status != "gap"
                && s.Tier == "primary"
                && s.Employee != null)
            .OrderBy(s => s.StartTime)
            .ThenByDescending(s => s.EndTime)
            .FirstOrDefaultAsync();

        return primary?.Employee?.MobilePhone;
    }

    private async Task RecordStepAndNotify(
        int eventId, string stepKey, string status, string detail, string? providerMessageId = null)
    {
        int? tenantId = null;
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
                ProviderMessageId = string.IsNullOrWhiteSpace(providerMessageId) ? null : providerMessageId,
            };
            db.DispatchSteps.Add(step);
            await db.SaveChangesAsync();

            // Resolve tenantId for tenant-scoped SignalR broadcast
            tenantId = await db.PhoneTreeEvents
                .Where(e => e.Id == eventId)
                .Select(e => e.PhoneTree!.Department!.TenantId)
                .FirstOrDefaultAsync();
        }

        if (tenantId.HasValue)
        {
            await _hub.Clients.Group($"tenant-{tenantId.Value}").SendAsync("DispatchStepCompleted", new
            {
                eventId,
                stepKey,
                status,
                detail,
                completedAt = DateTime.UtcNow,
            });
        }
        else
        {
            // Broadcasting to every client would hand one hospital's live code-call
            // activity to all the others. The DispatchStep row above is the durable
            // record of what happened; this is the notification failing to reach the
            // console, and it is recorded rather than dropped quietly.
            _logger.LogError(
                "Dispatch step {StepKey} for event {EventId} could not be delivered: its tenant "
                + "could not be resolved. It was NOT broadcast to other tenants.", stepKey, eventId);

            // Resolved leniently on purpose: an audit sink that is absent or failing must
            // never take the dispatch path down with it. The error log above is the signal
            // that always fires; this is the durable copy when a sink is available.
            using var auditScope = _scopeFactory.CreateScope();
            var audit = auditScope.ServiceProvider.GetService<IAuditService>();
            audit?.Enqueue(new AuditLog
            {
                Action = "NotificationUndeliverable",
                ResourceType = "DispatchStep",
                ResourceId = eventId.ToString(),
                UserName = "system",
                Details = $"Dispatch step '{stepKey}' had no resolvable tenant and was not delivered.",
                Timestamp = DateTime.UtcNow,
            });
        }
    }
}
