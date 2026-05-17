using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// LISTEN on the <c>partition_changes</c> Postgres channel and invalidate
/// <see cref="PgPartitionCache"/> entries when an <c>Admin/Partition/*</c>
/// row changes on any silo.
///
/// <para><b>Payload shape.</b> The trigger (see migration
/// <c>VxxPartitionNotifyChannel</c>) emits a JSON document:</para>
///
/// <code>
/// { "op": "INSERT" | "UPDATE" | "DELETE", "namespace": "..." }
/// </code>
///
/// <para>INSERT/UPDATE → call <see cref="PgPartitionCache.Invalidate"/> so
/// the next access re-probes <c>information_schema.schemata</c> (we don't
/// have the <see cref="MeshWeaver.Mesh.PartitionDefinition"/> in the
/// notification — the local workspace stream feeds that). DELETE → invalidate
/// + the next probe will return PendingCreate (or eventually Absent if we
/// add a strict mode).</para>
///
/// <para>Reconnects with exponential backoff if the connection drops.</para>
/// </summary>
public sealed class PgPartitionNotifyListener : IHostedService, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlPartitionStorageProvider _provider;
    private readonly ILogger<PgPartitionNotifyListener>? _logger;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public PgPartitionNotifyListener(
        NpgsqlDataSource dataSource,
        PostgreSqlPartitionStorageProvider provider,
        ILogger<PgPartitionNotifyListener>? logger = null)
    {
        _dataSource = dataSource;
        _provider = provider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenTask = ListenLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        if (_listenTask is not null)
        {
            try { await _listenTask; }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await _dataSource.OpenConnectionAsync(ct);
                conn.Notification += OnNotification;
                await using (var cmd = new NpgsqlCommand("LISTEN partition_changes", conn))
                    await cmd.ExecuteNonQueryAsync(ct);
                _logger?.LogInformation("PgPartitionNotifyListener: LISTEN started on partition_changes");
                backoff = TimeSpan.FromSeconds(1);
                while (!ct.IsCancellationRequested)
                    await conn.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "PgPartitionNotifyListener: connection broke; reconnecting in {Backoff}s",
                    backoff.TotalSeconds);
                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    private void OnNotification(object _, NpgsqlNotificationEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.Payload);
            if (!doc.RootElement.TryGetProperty("namespace", out var nsEl)) return;
            var ns = nsEl.GetString();
            if (string.IsNullOrEmpty(ns)) return;

            // The Admin/Partition row's id IS the partition namespace (per
            // the partition-routing contract). Invalidate the cache; next
            // probe re-runs information_schema.schemata.
            _provider.PartitionCache.Invalidate(ns);
            _logger?.LogDebug(
                "PgPartitionNotifyListener: invalidated cache for partition '{Ns}' ({Op})",
                ns,
                doc.RootElement.TryGetProperty("op", out var opEl) ? opEl.GetString() : "?");
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "PgPartitionNotifyListener: failed to parse payload");
        }
    }
}
