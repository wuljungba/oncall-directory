using OnCallApi.Data;

namespace OnCallApi.Services;

/// <summary>
/// Periodically checks active shifts and fires escalations based on configured policies.
/// Runs every 2 minutes (matching typical max response times of 5-15 minutes).
/// </summary>
public class EscalationBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<EscalationBackgroundService> _logger;
    private int _consecutiveFailures;
    private const int FailureThresholdForAlert = 3;

    public EscalationBackgroundService(IServiceProvider services, ILogger<EscalationBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
        _consecutiveFailures = 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<EscalationService>();
                await svc.CheckAndEscalateAsync();
                _consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                var level = _consecutiveFailures >= FailureThresholdForAlert ? LogLevel.Critical : LogLevel.Error;
                _logger.Log(level, ex,
                    "Escalation check cycle failed (consecutive failures: {FailureCount}). " +
                    "If this persists, the escalation engine may not be paging responders.",
                    _consecutiveFailures);
            }
        }
    }
}
