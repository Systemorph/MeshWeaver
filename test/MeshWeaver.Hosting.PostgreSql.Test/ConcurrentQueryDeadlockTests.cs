using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Concurrency soundness for <see cref="PostgreSqlMeshQuery"/> after the
/// bare-<c>Observable.FromAsync</c> → <c>IIoPool.Invoke</c> migration.
///
/// <para>The defect class (Doc/Architecture/AsynchronousCalls.md → "No bare
/// Observable.FromAsync"): a query leaf bridged with a bare
/// <c>Observable.FromAsync</c> runs its prologue on the SUBSCRIBING thread and
/// its <c>await</c> continuations on whatever scheduler the awaited task
/// captured. When such a leaf is consumed by a BLOCKING subscriber — a hub/grain
/// ActionBlock, or a test's synchronous <c>Should().Within(...)</c> wait — under
/// a <c>CombineLatest</c> provider fan-out, the continuation can be queued behind
/// the very thread that is blocked waiting for it. The symptom is the recurring
/// "snapshot query hangs", which the (now-deleted) initial-Full watchdog tried to
/// paper over and stormed instead. Routing is the production hot path for this:
/// every request resolves a path through an auth-less <c>Query&lt;MeshNode&gt;</c>.</para>
///
/// <para>This test hammers the migrated path with many CONCURRENT
/// routing-shape queries, each consumed by a BLOCKING <c>.Emit()</c> wait, and
/// asserts they ALL complete inside a bound. Because each leaf now runs inside
/// the <see cref="MeshWeaver.Mesh.Threading.IIoPool"/> (offloaded to the
/// ThreadPool with <c>ConfigureAwait(false)</c> on every await, slot released on
/// completion), the awaits release their pool threads while the DB round-trip is
/// in flight — so N blocking subscribers all make progress instead of starving
/// each other's continuations. A regression to a bare <c>FromAsync</c> would let
/// this wedge under load; the bounded <c>Task.WaitAll</c> turns that wedge into a
/// failed assertion rather than a hung test run.</para>
/// </summary>
[Collection("PostgreSql")]
public class ConcurrentQueryDeadlockTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public ConcurrentQueryDeadlockTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private const string Partition = "Concur";
    private const int NodeCount = 24;
    // Comfortably above the ThreadPool's min worker count so a wedged leaf would
    // actually starve rather than be masked by spare threads.
    private const int Concurrency = 48;

    private void Seed()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;
        for (var i = 0; i < NodeCount; i++)
        {
            adapter.Write(new MeshNode($"Node{i}", Partition)
            {
                Name = $"Node {i}",
                NodeType = "Story"
            }, _options).Should().Within(30.Seconds()).Emit();
        }
        _fixture.AccessControl.Grant(Partition, "Anonymous", "Read", isAllow: true, ct)
            .Should().Within(30.Seconds()).Emit();
    }

    [Fact(Timeout = 120_000)]
    public void Query_ManyConcurrentBlockingSubscribers_AllComplete_NoDeadlock()
    {
        Seed();

        // Capture the token on the TEST thread — TestContext.Current is not valid
        // on a ThreadPool worker, so reading it inside Task.Run throws
        // "no currently active test".
        var ct = TestContext.Current.CancellationToken;

        // A fresh provider per subscriber mirrors the per-call query surface and
        // keeps each subscription independent (the production aggregator opens one
        // provider Query() per request).
        var faults = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var completed = 0;

        var tasks = Enumerable.Range(0, Concurrency).Select(_ => Task.Run(() =>
        {
            try
            {
                var query = new PostgreSqlMeshQuery(_fixture.StorageAdapter);
                var request = MeshQueryRequest.FromQuery($"path:{Partition} scope:descendants");
                // BLOCKING subscriber — the shape that wedges a bare FromAsync.
                var results = query.QueryList(request, _options, ct)
                    .Should().Within(30.Seconds()).Emit();
                results.Should().NotBeNull();
                results.Should().HaveCount(NodeCount);
                Interlocked.Increment(ref completed);
            }
            catch (Exception ex)
            {
                faults.Add(ex);
            }
        }, ct)).ToArray();

        // Bounded join: a wedge becomes a failed assertion, never a hung run.
        // xUnit1031 (no blocking task ops) is INTENTIONALLY suppressed here — the
        // blocking join IS the test: it asserts the migrated path does NOT deadlock
        // under blocking subscribers. The timeout makes the block itself bounded.
#pragma warning disable xUnit1031
        var allDone = Task.WaitAll(tasks, TimeSpan.FromSeconds(90));
#pragma warning restore xUnit1031

        allDone.Should().BeTrue(
            $"all {Concurrency} concurrent blocking query subscribers must complete through the regular IIoPool infra " +
            $"({completed}/{Concurrency} finished) — a wedge here is the bare-Observable.FromAsync deadlock returning");
        faults.Should().BeEmpty("no concurrent query subscriber should fault");
    }
}
