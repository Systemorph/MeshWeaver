using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// <see cref="IHostedService"/> wrapper that auto-starts the
/// <see cref="SnowflakeChangeFeedPoller"/> at host startup — the Snowflake counterpart of
/// <c>PostgreSqlChangeListenerHostedService</c>. Without it the poller is registered as a DI
/// singleton but never activated: foreign silos' writes would surface as
/// <see cref="MeshWeaver.Mesh.Services.DataChangeNotification"/> events only while the poll
/// loop runs, and synced queries over Snowflake-backed namespaces would stay frozen at their
/// initial value. Registration (and the poller's target attachment) happens in
/// <c>SnowflakeExtensions</c>; this wrapper only drives the lifecycle.
/// </summary>
internal sealed class SnowflakeChangeFeedPollerHostedService : IHostedService
{
    private readonly SnowflakeChangeFeedPoller _poller;
    private readonly SnowflakeStorageOptions _options;
    private readonly ILogger<SnowflakeChangeFeedPollerHostedService>? _logger;

    /// <summary>Creates the wrapper over the DI-registered poller singleton.</summary>
    /// <param name="poller">The poller to start/stop with the host.</param>
    /// <param name="options">Honours <see cref="SnowflakeStorageOptions.EnableChangeFeedPolling"/> even when the service was registered unconditionally.</param>
    /// <param name="logger">Optional logger for lifecycle diagnostics.</param>
    public SnowflakeChangeFeedPollerHostedService(
        SnowflakeChangeFeedPoller poller,
        SnowflakeStorageOptions options,
        ILogger<SnowflakeChangeFeedPollerHostedService>? logger = null)
    {
        _poller = poller;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Starts the poll loop (unless <see cref="SnowflakeStorageOptions.EnableChangeFeedPolling"/>
    /// is off — polling keeps the warehouse warm, so the flag is the cost switch). The target
    /// observer is attached before this runs:
    /// <c>// wired in SnowflakeExtensions: poller.Attach(routingAdapter.ChangeObserver)</c>.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableChangeFeedPolling)
        {
            _logger?.LogInformation(
                "Snowflake change-feed polling is disabled (EnableChangeFeedPolling=false); cross-process change propagation is off");
            return Task.CompletedTask;
        }
        _logger?.LogInformation("Starting SnowflakeChangeFeedPoller");
        _poller.Start();
        return Task.CompletedTask;
    }

    /// <summary>Stops the poll loop by disposing its interval subscription.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Stopping SnowflakeChangeFeedPoller");
        _poller.Stop();
        return Task.CompletedTask;
    }
}
