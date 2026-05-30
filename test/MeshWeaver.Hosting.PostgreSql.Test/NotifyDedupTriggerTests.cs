using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using Npgsql;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Verifies notify_mesh_node_changes() suppresses pg_notify on UPDATEs that
/// don't change any reactive consumer-visible field. Prod incident 2026-05-20:
/// same-value upserts on access checks fired NOTIFY per call, every NOTIFY
/// woke every synced query, which re-read, which triggered more same-value
/// writes -- amplification loop that melted the per-partition single-
/// connection pool.
///
/// Assertions measure the DELTA in received notifications around each
/// action, not absolute counts. Fixture init / cleanup may emit unrelated
/// NOTIFYs into the queue; what we care about is whether the action under
/// test fires a NOTIFY or not.
/// </summary>
[Collection("PostgreSqlIsolated")]
public class NotifyDedupTriggerTests(IsolatedPostgreSqlFixture fixture) : IAsyncLifetime
{
    private readonly JsonSerializerOptions _options = new();
    private NpgsqlConnection _listenConn = null!;
    private readonly List<string> _received = new();
    private CancellationTokenSource _listenCts = null!;
    private Task _listenTask = null!;

    public async ValueTask InitializeAsync()
    {
        await fixture.CleanDataAsync();
        _listenConn = await fixture.DataSource.OpenConnectionAsync();
        _listenConn.Notification += (_, e) =>
        {
            lock (_received) _received.Add(e.Payload ?? "");
        };
        await using (var cmd = new NpgsqlCommand("LISTEN mesh_node_changes", _listenConn))
            await cmd.ExecuteNonQueryAsync();

        _listenCts = new CancellationTokenSource();
        _listenTask = Task.Run(async () =>
        {
            try
            {
                while (!_listenCts.IsCancellationRequested)
                    await _listenConn.WaitAsync(_listenCts.Token);
            }
            catch (OperationCanceledException) { }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _listenCts.Cancel();
        try { await _listenTask; } catch { }
        await _listenConn.DisposeAsync();
    }

    private int Count() { lock (_received) return _received.Count; }

    private async Task<int> NewNotificationsSinceAsync(int baseline, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var delta = Count() - baseline;
            if (delta > 0) return delta;
            await Task.Delay(20);
        }
        return Count() - baseline;
    }

    private async Task<int> NewNotificationsAfterDelay(int baseline, TimeSpan window)
    {
        await Task.Delay(window);
        return Count() - baseline;
    }

    [Fact]
    public async Task NoOpUpdate_SameContent_DoesNotFireNotify()
    {
        var ct = TestContext.Current.CancellationToken;
        var node = new MeshNode("dedup-target", "tests")
        {
            Name = "Dedup Target",
            NodeType = "Settings",
            Content = new Dictionary<string, object> { { "k", "v1" } },
            State = MeshNodeState.Active,
            Version = 1,
        };

        // INSERT must fire at least one NOTIFY.
        var beforeInsert = Count();
        await fixture.StorageAdapter.WriteAsync(node, _options, ct);
        var insertDelta = await NewNotificationsSinceAsync(beforeInsert, TimeSpan.FromSeconds(2));
        insertDelta.Should().BeGreaterThanOrEqualTo(1, "INSERT must fire NOTIFY");

        // Same-value UPSERT must NOT fire any NOTIFY in a quiet window.
        var beforeNoOp = Count();
        await fixture.StorageAdapter.WriteAsync(node, _options, ct);
        var noOpDelta = await NewNotificationsAfterDelay(beforeNoOp, TimeSpan.FromMilliseconds(500));
        noOpDelta.Should().Be(0,
            "same-value UPDATE must NOT fire NOTIFY -- that's the dedup that "
            + "breaks the prod feedback loop");
    }

    [Fact]
    public async Task RealUpdate_DifferentContent_FiresNotify()
    {
        var ct = TestContext.Current.CancellationToken;
        var node = new MeshNode("real-update", "tests")
        {
            Name = "First",
            NodeType = "Settings",
            Content = new Dictionary<string, object> { { "k", "v1" } },
            State = MeshNodeState.Active,
            Version = 1,
        };
        await fixture.StorageAdapter.WriteAsync(node, _options, ct);
        await NewNotificationsSinceAsync(0, TimeSpan.FromSeconds(2));

        var beforeUpdate = Count();
        var updated = node with
        {
            Content = new Dictionary<string, object> { { "k", "v2" } },
            Version = 2,
        };
        await fixture.StorageAdapter.WriteAsync(updated, _options, ct);
        var updateDelta = await NewNotificationsSinceAsync(beforeUpdate, TimeSpan.FromSeconds(2));
        updateDelta.Should().BeGreaterThanOrEqualTo(1,
            "UPDATE with different content + version must fire NOTIFY");
    }

    [Fact]
    public async Task NameChange_AloneFiresNotify()
    {
        var ct = TestContext.Current.CancellationToken;
        var node = new MeshNode("name-change", "tests")
        {
            Name = "Before",
            NodeType = "Settings",
            Content = new Dictionary<string, object> { { "k", "v" } },
            State = MeshNodeState.Active,
            Version = 1,
        };
        await fixture.StorageAdapter.WriteAsync(node, _options, ct);
        await NewNotificationsSinceAsync(0, TimeSpan.FromSeconds(2));

        var beforeRename = Count();
        var renamed = node with { Name = "After" };
        await fixture.StorageAdapter.WriteAsync(renamed, _options, ct);
        var renameDelta = await NewNotificationsSinceAsync(beforeRename, TimeSpan.FromSeconds(2));
        renameDelta.Should().BeGreaterThanOrEqualTo(1,
            "Name-only change must still fire NOTIFY -- UI display-name "
            + "bindings depend on it");
    }

    [Fact]
    public async Task DeleteFiresNotify()
    {
        var ct = TestContext.Current.CancellationToken;
        var node = new MeshNode("delete-target", "tests")
        {
            Name = "Doomed",
            NodeType = "Settings",
            State = MeshNodeState.Active,
            Version = 1,
        };
        await fixture.StorageAdapter.WriteAsync(node, _options, ct);
        await NewNotificationsSinceAsync(0, TimeSpan.FromSeconds(2));

        var beforeDelete = Count();
        await fixture.StorageAdapter.DeleteAsync(node.Path, ct);
        var deleteDelta = await NewNotificationsSinceAsync(beforeDelete, TimeSpan.FromSeconds(2));
        deleteDelta.Should().BeGreaterThanOrEqualTo(1, "DELETE must always fire NOTIFY");

        // The most recent NOTIFY for this path should carry op=DELETE.
        string? lastPayloadForPath = null;
        lock (_received)
            for (int i = _received.Count - 1; i >= 0; i--)
            {
                var p = _received[i];
                if (p.Contains("delete-target"))
                {
                    lastPayloadForPath = p;
                    break;
                }
            }
        lastPayloadForPath.Should().NotBeNull();
        var json = JsonDocument.Parse(lastPayloadForPath!);
        json.RootElement.GetProperty("op").GetString().Should().Be("DELETE");
    }
}
