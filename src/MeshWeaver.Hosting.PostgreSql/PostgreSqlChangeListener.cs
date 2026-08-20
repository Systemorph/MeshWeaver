using System.Text.Json;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Listens for PostgreSQL LISTEN/NOTIFY on the mesh_node_changes channel
/// and publishes DataChangeNotification events to <see cref="IObserver{T}"/> of <see cref="DataChangeNotification"/>.
/// </summary>
public class PostgreSqlChangeListener : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    // Non-null only when this listener BUILT its data source (see OwningDataSource) and must
    // therefore dispose it. A data source handed in from outside belongs to its owner.
    private readonly NpgsqlDataSource? _ownedDataSource;
    private readonly IObserver<DataChangeNotification> _changeNotifier;
    private readonly ILogger<PostgreSqlChangeListener>? _logger;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    /// <summary>
    /// Initializes the change listener.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source used to open the dedicated LISTEN connection.</param>
    /// <param name="changeNotifier">Observer that receives a <see cref="DataChangeNotification"/> for each NOTIFY payload.</param>
    /// <param name="logger">Optional logger for listener lifecycle and error diagnostics.</param>
    public PostgreSqlChangeListener(
        NpgsqlDataSource dataSource,
        IObserver<DataChangeNotification> changeNotifier,
        ILogger<PostgreSqlChangeListener>? logger = null)
    {
        _dataSource = dataSource;
        _changeNotifier = changeNotifier;
        _logger = logger;
    }

    /// <summary>
    /// Builds a listener that OWNS <paramref name="dedicatedDataSource"/> and disposes it with
    /// itself — the shape the partitioned wiring uses, where
    /// <c>PostgreSqlPartitionStorageProvider.CreateChangeListenerDataSource</c> builds a
    /// single-connection source configured exactly like every other source that provider builds.
    ///
    /// <para>🚨 The <c>LISTEN</c> session holds one connection open for the life of the process, so
    /// taking it from the shared pool permanently removes a connection every read and write
    /// competes for — on a pool the portal has already exhausted once ("the connection pool has
    /// been exhausted (currently 50)"). A dedicated source keeps that cost off the pool that serves
    /// queries and names the session in <c>pg_stat_activity</c>, so a permanently-idle connection
    /// is identifiable rather than suspicious.</para>
    /// </summary>
    /// <param name="dedicatedDataSource">The listener's own data source; disposed with the listener.</param>
    /// <param name="changeNotifier">Observer that receives a notification per NOTIFY payload.</param>
    /// <param name="logger">Optional logger for listener lifecycle and error diagnostics.</param>
    internal static PostgreSqlChangeListener OwningDataSource(
        NpgsqlDataSource dedicatedDataSource,
        IObserver<DataChangeNotification> changeNotifier,
        ILogger<PostgreSqlChangeListener>? logger = null)
        => new(dedicatedDataSource, changeNotifier, logger, ownsDataSource: true);

    private PostgreSqlChangeListener(
        NpgsqlDataSource dataSource,
        IObserver<DataChangeNotification> changeNotifier,
        ILogger<PostgreSqlChangeListener>? logger,
        bool ownsDataSource)
        : this(dataSource, changeNotifier, logger)
    {
        if (ownsDataSource)
            _ownedDataSource = dataSource;
    }

    /// <summary>
    /// Starts the background listener loop.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenTask = ListenLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the background listener.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_listenTask != null)
            {
                try
                {
                    await _listenTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

                // Subscribe to notification event
                conn.Notification += OnNotification;

                await using (var listenCmd = new NpgsqlCommand("LISTEN mesh_node_changes", conn))
                {
                    await listenCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                _logger?.LogInformation("PostgreSQL LISTEN started on mesh_node_changes");

                // WaitAsync will block until a notification arrives or cancellation is requested
                while (!ct.IsCancellationRequested)
                {
                    await conn.WaitAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "LISTEN connection error, reconnecting in 5s");
                try
                {
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(e.Payload))
                return;

            var payload = JsonSerializer.Deserialize<JsonElement>(e.Payload);
            var path = payload.GetProperty("path").GetString() ?? "";
            var op = payload.GetProperty("op").GetString() ?? "";

            var kind = op switch
            {
                "INSERT" => DataChangeKind.Created,
                "UPDATE" => DataChangeKind.Updated,
                "DELETE" => DataChangeKind.Deleted,
                _ => DataChangeKind.Updated
            };

            _changeNotifier.OnNext(new DataChangeNotification(path, kind, null, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing notification: {Payload}", e.Payload);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
        if (_ownedDataSource is not null)
            await _ownedDataSource.DisposeAsync().ConfigureAwait(false);
    }
}
