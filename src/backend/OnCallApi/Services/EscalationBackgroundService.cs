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

    public EscalationBackgroundService(IServiceProvider services, ILogger<EscalationBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Escalation check cycle failed");
            }
        }
    }
}
