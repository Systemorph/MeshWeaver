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

    // Completed the instant `LISTEN mesh_node_changes` has actually executed — see Listening.
    // AsyncSubject: it replays its completion to every later subscriber, so a consumer that asks
    // after registration already happened is answered immediately rather than waiting for an
    // announcement that has been and gone.
    private readonly System.Reactive.Subjects.AsyncSubject<System.Reactive.Unit> _listening = new();

    /// <summary>
    /// Completes once <c>LISTEN mesh_node_changes</c> has been REGISTERED on the dedicated
    /// connection — i.e. from this point on a <c>NOTIFY</c> is actually delivered here.
    ///
    /// <para>🚨 <b>Why this is not the same as "StartAsync returned", and why nothing else can
    /// stand in for it.</b> <see cref="StartAsync"/> deliberately returns as soon as the loop is
    /// launched (a hosted service must not block host startup on a database being reachable), and
    /// the loop then opens a connection FIRST and issues <c>LISTEN</c> second. In that window the
    /// process is connected but not subscribed — and <b>Postgres never replays a
    /// <c>NOTIFY</c></b>, so every notification fired in it is lost with no recovery path.</para>
    ///
    /// <para>The connection is therefore NOT a proxy for the subscription: a backend appears in
    /// <c>pg_stat_activity</c> the moment it opens, which is exactly why the readiness probe that
    /// counted rows there could go green before <c>LISTEN</c> had run and let a writer race it
    /// (Systemorph/MeshWeaver#2281). Anything that must not miss the first notification waits on
    /// THIS, which is signalled by the <c>LISTEN</c> statement itself completing.</para>
    ///
    /// <para>Completes on the FIRST successful registration only. A later reconnect re-issues
    /// <c>LISTEN</c> but does not re-signal — this answers "has this listener ever come up", not
    /// "is the connection healthy right now".</para>
    /// </summary>
    public IObservable<System.Reactive.Unit> Listening => _listening;

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
    ///
    /// <para>🚨 Returns as soon as the loop is LAUNCHED — deliberately, because this runs from an
    /// <c>IHostedService</c> and a portal must not fail to start because the database is briefly
    /// unreachable (the loop reconnects on its own). So "StartAsync completed" does NOT mean
    /// "listening": wait on <see cref="Listening"/> for that, and read its remarks for why the
    /// difference is a real, silent data-loss window rather than a technicality.</para>
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
                // The subscription now exists on the server, so from here a NOTIFY reaches us.
                // Signalled AFTER ExecuteNonQueryAsync, never after OpenConnectionAsync — the
                // whole point of Listening is that those two are not the same instant.
                _listening.OnNext(System.Reactive.Unit.Default);
                _listening.OnCompleted();

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
