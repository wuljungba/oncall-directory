using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OnCallApi.Services;

namespace BackendTests.Services;

/// <summary>
/// An interval of 0 means the service does no work at all.
///
/// Only AD sync honoured that. Presence sync clamped the value — `Math.Max(intervalMin, 1)`
/// — so the setting meant to switch it off made it the busiest service of the four, and
/// department and calendar sync had no gate whatsoever: they ran their first cycle seconds
/// after startup wherever they happened to be running. On a developer's machine that is
/// app-only authentication against the configured tenant, a real directory pulled locally,
/// and in calendar sync's case writes to real mailboxes.
///
/// These assert the off switch, not the sync logic: a disabled service must finish its
/// execute task immediately, having scheduled nothing.
/// </summary>
public class SyncServiceDisabledTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    /// <summary>Empty on purpose: a disabled service must never resolve anything from it.</summary>
    private static IServiceProvider EmptyProvider() => new ServiceCollection().BuildServiceProvider();

    /// <summary>
    /// Finishes immediately, rather than on the very same thread: the host is free to hop
    /// threads starting a hosted service, so "IsCompleted right after StartAsync" is not the
    /// contract. The window is still decisive — every one of these services, left enabled,
    /// waits 10 to 45 seconds before its first cycle and then loops forever.
    /// </summary>
    private static async Task AssertDoesNothing(BackgroundService service)
    {
        await service.StartAsync(CancellationToken.None);

        service.ExecuteTask.Should().NotBeNull();
        var finished = await Task.WhenAny(service.ExecuteTask!, Task.Delay(TimeSpan.FromSeconds(2)));
        finished.Should().BeSameAs(
            service.ExecuteTask,
            "a disabled sync service must return without scheduling a timer or a first cycle");

        await service.ExecuteTask!; // surfaces a fault instead of swallowing it
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AdSyncDoesNothingAtZero()
    {
        var service = new AdSyncBackgroundService(
            EmptyProvider(),
            Config(("Sync:AdSyncIntervalMinutes", "0")),
            NullLogger<AdSyncBackgroundService>.Instance);

        await AssertDoesNothing(service);
    }

    [Fact]
    public async Task DepartmentSyncDoesNothingAtZero()
    {
        var service = new DepartmentSyncService(
            EmptyProvider(),
            Config(("Sync:DepartmentSyncIntervalMinutes", "0")),
            NullLogger<DepartmentSyncService>.Instance);

        await AssertDoesNothing(service);
    }

    /// <summary>The one that used to read 0 as "every 60 seconds".</summary>
    [Fact]
    public async Task PresenceSyncDoesNothingAtZero()
    {
        var service = new PresenceSyncService(
            EmptyProvider(),
            Config(("Sync:PresenceSyncIntervalMinutes", "0")),
            NullLogger<PresenceSyncService>.Instance);

        await AssertDoesNothing(service);
    }

    [Fact]
    public async Task CalendarSyncDoesNothingAtZero()
    {
        var service = new CalendarSyncService(
            EmptyProvider(),
            Config(("Sync:CalendarSyncIntervalMinutes", "0")),
            NullLogger<CalendarSyncService>.Instance);

        await AssertDoesNothing(service);
    }

    /// <summary>
    /// The inverse, so the assertions above cannot pass by observing nothing: an enabled
    /// service is still running after the same window. Presence sync waits 10 seconds before
    /// its first cycle, so nothing reaches Graph inside the 2 seconds this watches.
    /// </summary>
    [Fact]
    public async Task AnEnabledServiceIsStillRunningAfterTheSameWindow()
    {
        var service = new PresenceSyncService(
            EmptyProvider(),
            Config(("Sync:PresenceSyncIntervalMinutes", "5")),
            NullLogger<PresenceSyncService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var finished = await Task.WhenAny(service.ExecuteTask!, Task.Delay(TimeSpan.FromSeconds(2)));
        finished.Should().NotBeSameAs(
            service.ExecuteTask,
            "an enabled service schedules its first cycle and keeps running");

        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// A negative value is the same as off, not an exception or a tight loop — the services
    /// read "&lt;= 0", and a hand-edited config is where a -1 comes from.
    /// </summary>
    [Fact]
    public async Task ANegativeIntervalIsAlsoOff()
    {
        var service = new PresenceSyncService(
            EmptyProvider(),
            Config(("Sync:PresenceSyncIntervalMinutes", "-1")),
            NullLogger<PresenceSyncService>.Instance);

        await AssertDoesNothing(service);
    }
}
