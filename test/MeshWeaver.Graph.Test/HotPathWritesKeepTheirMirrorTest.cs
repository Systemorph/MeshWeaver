using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Issue #1174, end to end on a REAL mesh: a node written over and over from another hub — the
/// shape of <c>{user}/_UserActivity/{user}</c>, written on every cold page load — must be written
/// through ONE mirror, and every write must still land.
///
/// <para><b>What was wrong.</b> The change feed's <c>Updated</c> for each commit evicted every
/// cached mirror of the owner, including the one the writer had just used, so the NEXT write
/// resolved a brand-new mirror: a <c>SubscribeRequest</c>, an initial-state round trip and a fresh
/// pair of <c>sync/</c> hubs, per write, each of which had to hydrate inside the writer's 30 s
/// base-state bound while its predecessor was torn down on the same owner address. On a hot path
/// that is thousands of hydrations — the hot-path concentration of #1174's 414 production
/// timeouts. The writer's base is now held to the VERSION the commit announced instead, so the
/// mirror can stay.</para>
///
/// <para><b>Why this test exists beside the two seam tests</b>
/// (<c>HotPathWriteBaseIsVersionAwareTest</c>, <c>HotPathMirrorChurnTest</c>). Both of those feed
/// the workspace a SYNTHETIC version. The design only works if the version the real change feed
/// announces is the version the real owner fans out to the mirror — if the two clocks disagreed,
/// every hot write would sit out the catch-up bound and then evict, which is strictly worse than
/// what it replaces. This is the arm that can see that: it asserts not only "no new mirror" but
/// "no <c>MIRROR_BEHIND</c>", and it proves the floor was actually in force ("kept" lines), so a
/// green cannot come from a build where the change feed never reached the workspace.</para>
///
/// <para>Measured through the workspace's and the write path's OWN diagnostic lines, captured by a
/// provider the mesh owns — the one instrument that sees every mirror a write opens, whichever
/// identity it opens it under.</para>
/// </summary>
public class HotPathWritesKeepTheirMirrorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Sequential writes to the one node. Small: the defect is monotone (one fresh mirror
    /// per write), so a handful separates "one mirror" from "one per write".</summary>
    private const int Writes = 6;

    // 🚨 INSTANCE, never static — a static provider would be process-wide mutable state that
    // survives mesh disposal and bleeds across tests. A derived field initialiser runs before the
    // base constructor calls ConfigureMesh, so it is ready when needed.
    private readonly DiagnosticLineCapture captured = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddSingleton<ILoggerProvider>(captured)
                // Both lines this test counts are LogDebug ON PURPOSE (one per mirror, one per
                // commit — hot-path cost paid only when asked). src/ log levels are committed
                // contract, so the test opts IN for exactly these two categories instead.
                .Configure<LoggerFilterOptions>(o =>
                {
                    o.Rules.Add(new LoggerFilterRule(null, DiagnosticLineCapture.WorkspaceCategory,
                        LogLevel.Debug, null));
                    o.Rules.Add(new LoggerFilterRule(null, DiagnosticLineCapture.WritePathCategory,
                        LogLevel.Debug, null));
                }));

    // 600_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant. It
    // must still DOMINATE every inner wait below (TestTimeouts.Convergence per step, 108 s at the
    // CI factor), or the xunit kill pre-empts the wait and the failure cannot name what it was
    // waiting for.
    [Fact(Timeout = 600_000)]
    public async Task SequentialWritesToOneNode_ReuseOneMirror_AndEveryWriteLands()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var workspace = Mesh.GetWorkspace();

        var id = "hot" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{TestPartition}/{id}";

        await meshService.CreateNode(new MeshNode(id, TestPartition)
        {
            Name = "Hot path",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new UserActivityRecord
            {
                Id = id,
                NodePath = path,
                UserId = "hot-path-probe",
                ActivityType = ActivityType.Read,
                AccessCount = 0,
            },
        }).Take(1).Should().Within(TestTimeouts.Convergence)
            .Emit("the node the writes target must exist first", cancellationToken: ct);

        var stream = workspace.GetMeshNodeStream(path);

        // The exact shape HandleTrackActivity's FoldOntoLive uses: fold onto the LIVE node inside
        // the lambda, so the count this test reads back is the number of writes that LANDED.
        MeshNode Fold(MeshNode live)
        {
            var rec = live.ContentAs<UserActivityRecord>(Mesh.JsonSerializerOptions)
                      ?? new UserActivityRecord { Id = id, NodePath = path };
            return live with
            {
                Content = rec with
                {
                    AccessCount = rec.AccessCount + 1,
                    LastAccessedAt = DateTimeOffset.UtcNow,
                },
            };
        }

        // ── Write 1 warms the writer's mirror. ──────────────────────────────────────────────────
        await stream.Update(Fold).Should().Within(TestTimeouts.Convergence)
            .Emit("the first write must land", cancellationToken: ct);

        // POSITIVE CONTROL of the instrument: the first write DID open a mirror for this path, and
        // the capture saw it. Without this, "zero opened" below would also pass on a build whose
        // Workspace category never reached the provider.
        captured.Count(DiagnosticLineCapture.OpenedFor(path)).Should().BeGreaterThan(0,
            "the first cross-hub write opens this hub's mirror of the node, and the capture must "
            + "see that line — otherwise every count below is blind");

        // …and write 1's commit must have REACHED the workspace before the hot path is measured —
        // whichever way the workspace answered it (kept the mirror under a floor, or evicted it).
        // Waited for, not assumed: the change feed publishes after the durable flush, which can
        // trail the write's own verdict. Either answer ends the wait on purpose, so a build that
        // still evicts per commit fails on the CHURN it causes below, which is the fact that
        // matters, rather than on a missing log line.
        await captured.FirstMatching(DiagnosticLineCapture.ChangeFeedAnsweredFor(path), TestTimeouts.Convergence)
            .Should().Emit("write 1's commit must reach this hub's workspace through the change feed",
                cancellationToken: ct);

        var openedBefore = captured.Count(DiagnosticLineCapture.OpenedFor(path));
        var keptBefore = captured.Count(DiagnosticLineCapture.KeptFor(path));

        // ── Writes 2..N: the hot path. ──────────────────────────────────────────────────────────
        for (var write = 2; write <= Writes; write++)
        {
            await stream.Update(Fold).Should().Within(TestTimeouts.Convergence)
                .Emit($"write {write} must land", cancellationToken: ct);
        }

        var final = await ReadNode(path).Should().Match(
            n => n is not null
                 && n.ContentAs<UserActivityRecord>(Mesh.JsonSerializerOptions)?.AccessCount == Writes,
            $"all {Writes} writes must land on the node", cancellationToken: ct);

        var openedDuringHotPath = captured.Count(DiagnosticLineCapture.OpenedFor(path)) - openedBefore;
        var keptDuringHotPath = captured.Count(DiagnosticLineCapture.KeptFor(path)) - keptBefore;
        var mirrorBehind = captured.Count(DiagnosticLineCapture.MirrorBehindFor(path));

        Output.WriteLine(
            $"DIAG hot path: writes={Writes} openedAfterWarmup={openedDuringHotPath} "
            + $"keptFloors={keptDuringHotPath} mirrorBehind={mirrorBehind} "
            + $"accessCount={final!.ContentAs<UserActivityRecord>(Mesh.JsonSerializerOptions)?.AccessCount}");

        openedDuringHotPath.Should().Be(0,
            $"{Writes - 1} writes after the first must reuse the mirror the first one opened. One "
            + "fresh mirror per write is #1174: a SubscribeRequest, an initial-state round trip and a "
            + "pair of sync/ hubs each, every one of which has to hydrate inside the writer's 30 s "
            + "base-state bound while its predecessor is torn down on the same owner address");

        mirrorBehind.Should().Be(0,
            "🚨 the version the change feed announces must be the version the owner fans out to the "
            + "mirror. A MIRROR_BEHIND on a healthy in-process owner means the two clocks disagree, "
            + "and then EVERY hot write sits out the catch-up bound and evicts — strictly worse than "
            + "the per-write eviction this replaced");

        keptDuringHotPath.Should().BeGreaterThan(0,
            "the later commits must also have reached the workspace as kept floors — a zero here "
            + "would mean the writes ran with no floor at all, i.e. the version-aware base was never "
            + "exercised and the reuse above proves nothing about it");
    }

    /// <summary>Captures the two diagnostic channels this test counts. Instance state on a
    /// provider the mesh owns — never static mutable state, which would bleed across tests.</summary>
    private sealed class DiagnosticLineCapture : ILoggerProvider
    {
        /// <summary><c>ILogger&lt;Workspace&gt;</c> — "opened remote stream" / "Mirror … kept".</summary>
        public const string WorkspaceCategory = "MeshWeaver.Data.Workspace";

        /// <summary>The write path's own channel — <c>MIRROR_BEHIND</c>.</summary>
        public const string WritePathCategory = "MeshWeaver.Mesh.MeshNodeStreamHandle";

        private readonly ConcurrentQueue<string> lines = new();

        public static Regex OpenedFor(string path)
            => new($@"opened remote stream \S+ for {Regex.Escape(path)} as ");

        public static Regex KeptFor(string path)
            => new($@"^Mirror for {Regex.Escape(path)} kept; the change feed announced version \d+");

        /// <summary>Either answer the workspace can give a commit on a mirrored owner.</summary>
        public static Regex ChangeFeedAnsweredFor(string path)
            => new($@"^(Mirror for {Regex.Escape(path)} kept; |Evicted remote stream cache for {Regex.Escape(path)} after change event)");

        public static Regex MirrorBehindFor(string path)
            => new($@"\[UpdateRemote\] MIRROR_BEHIND .*target={Regex.Escape(path)} ");

        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
        public void Dispose() { }

        /// <summary>How many captured lines match — over a SNAPSHOT (<c>ToArray</c>), because the
        /// queue is written by hub threads while this reads it.</summary>
        public int Count(Regex pattern) => lines.ToArray().Count(pattern.IsMatch);

        /// <summary>Waits for a line matching <paramref name="pattern"/> by POLLING the buffer — a
        /// line logged before the call is seen, one logged after it is not missed, and the
        /// continuation resumes on the poller's thread, never on the hub thread that logged (the
        /// wedge <c>LateNackReenqueueCorrelationTest</c>'s capture documents).</summary>
        public IObservable<string> FirstMatching(Regex pattern, TimeSpan within)
            => Observable.Interval(50.Milliseconds()).StartWith(0L)
                .Select(_ => lines.ToArray().FirstOrDefault(pattern.IsMatch))
                .Where(line => line is not null)
                .Select(line => line!)
                .FirstAsync()
                .Timeout(within);

        private sealed class Sink(DiagnosticLineCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (category is WorkspaceCategory or WritePathCategory)
                    owner.lines.Enqueue(formatter(state, exception));
            }
        }
    }
}
