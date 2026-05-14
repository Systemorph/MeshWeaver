using System.Text.Json;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Listens for PostgreSQL LISTEN/NOTIFY on the mesh_node_changes channel
/// and publishes DataChangeNotification events to IDataChangeNotifier.
/// </summary>
public class PostgreSqlChangeListener : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IDataChangeNotifier _changeNotifier;
    private readonly ILogger<PostgreSqlChangeListener>? _logger;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public PostgreSqlChangeListener(
        NpgsqlDataSource dataSource,
        IDataChangeNotifier changeNotifier,
        ILogger<PostgreSqlChangeListener>? logger = null)
    {
        _dataSource = dataSource;
        _changeNotifier = changeNotifier;
        _logger = logger;
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
            await _cts.CancelAsync();
            if (_listenTask != null)
            {
                try
                {
                    await _listenTask;
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
                await using var conn = await _dataSource.OpenConnectionAsync(ct);

                // Subscribe to notification event
                conn.Notification += OnNotification;

                await using (var listenCmd = new NpgsqlCommand("LISTEN mesh_node_changes", conn))
                {
                    await listenCmd.ExecuteNonQueryAsync(ct);
                }

                _logger?.LogInformation("PostgreSQL LISTEN started on mesh_node_changes");

                // WaitAsync will block until a notification arrives or cancellation is requested
                while (!ct.IsCancellationRequested)
                {
                    await conn.WaitAsync(ct);
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
                    await Task.Delay(5000, ct);
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

            _changeNotifier.NotifyChange(new DataChangeNotification(path, kind, null, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error processing notification: {Payload}", e.Payload);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
