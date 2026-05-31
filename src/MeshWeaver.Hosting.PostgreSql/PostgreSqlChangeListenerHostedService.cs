using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// <see cref="IHostedService"/> wrapper that auto-starts
/// <see cref="PostgreSqlChangeListener"/> at host startup. Without this
/// wrapper the listener is registered as a DI singleton but is never
/// activated — pg_notify events fired by Postgres triggers surface as
/// <see cref="MeshWeaver.Mesh.Services.DataChangeNotification"/> events only
/// when the listener's <c>LISTEN</c> session is open. Synced queries
/// (<c>workspace.GetQuery(...)</c> over Postgres-backed namespaces) depend
/// on this propagation; without it, the cached <c>Replay(1)</c> entries
/// stay frozen at their initial value and never re-emit on writes.
/// </summary>
internal sealed class PostgreSqlChangeListenerHostedService : IHostedService
{
    private readonly PostgreSqlChangeListener _listener;
    private readonly ILogger<PostgreSqlChangeListenerHostedService>? _logger;

    public PostgreSqlChangeListenerHostedService(
        PostgreSqlChangeListener listener,
        ILogger<PostgreSqlChangeListenerHostedService>? logger = null)
    {
        _listener = listener;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Starting PostgreSqlChangeListener");
        return _listener.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("Stopping PostgreSqlChangeListener");
        await _listener.StopAsync();
    }
}
