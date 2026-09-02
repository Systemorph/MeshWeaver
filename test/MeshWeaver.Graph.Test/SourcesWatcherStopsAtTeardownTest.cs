using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Systemorph/MeshWeaver#3026 — <b>the sources watcher kept requesting against a hub that was
/// already 2 s into its teardown.</b>
///
/// <para>The measurement, from a <c>MeshWeaver.FutuRe.Test</c> shard: <c>DISPOSE_INVOKED</c>, then
/// 2 052 ms later a <c>[FAULT]</c> — <c>SourceIncludeUnavailableException ---&gt;
/// ObjectDisposedException: Hub FutuRe/LocalAnalysis was disposed before the response arrived
/// (GetDataRequest, target FutuRe/GroupAnalysis/Source/ExternalDependencies)</c> — then
/// <c>DISPOSE_DONE … teardown clean</c>. Twenty-two times in one shard, one per fixture teardown,
/// and once a sibling of that callback escaped on a raw thread and killed the host (exit 139).</para>
///
/// <para><b>The mechanism.</b> The watcher's <c>@@</c>-include read was IN FLIGHT when the hub
/// began tearing down. Its subscription was registered with <c>hub.RegisterForDisposal</c>, which
/// disposes registrants in the ShutDown phase — the LAST one — so through the Quiescing phase the
/// read stayed pending, Quiescing waited its whole 2 s budget for it, then <c>CancelCallbacks</c>
/// errored it with the <c>ObjectDisposedException</c> above. The fixture's own leak gate could not
/// see it either: the hub had left its parent's registry before the verdict was read.</para>
///
/// <para><b>This test reproduces the in-flight read deterministically.</b> The include target is
/// an instance of a NodeType whose hub never finishes initializing (a <c>WithInitialization</c>
/// that never completes), so a <c>GetDataRequest</c> to it is parked behind its init gate and the
/// reader's callback stays pending for the whole 15 s read budget. The NodeType hub is then
/// disposed while that read is outstanding. Post-fix the watcher lets go of the read at the first
/// instant of teardown, so Quiescing drains at once and no fault is logged; pre-fix the hub timed
/// out its quiesce budget and logged the very <c>[FAULT]</c> the issue quotes.</para>
/// </summary>
public class SourcesWatcherStopsAtTeardownTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A NodeType whose instance hubs never open their initialization gate.</summary>
    private const string GateNodeType = "IncludeGate";

    /// <summary>The include target — an instance of <see cref="GateNodeType"/>, so a read of it parks.</summary>
    private const string IncludePath = "Gate/Target";

    private const string TypePath = "Watched/Type";

    /// <summary>Emits once the gated hub has been ACTIVATED — i.e. the watcher's include read has
    /// reached it and is now parked behind its init gate. The positive signal that the in-flight
    /// read this test is about actually exists.</summary>
    private readonly AsyncSubject<Unit> gateActivated = new();

    /// <summary>The gated hub's initialization: completed by nobody, so its gate never opens while
    /// the test runs. Completed in the test's <c>finally</c> so the fixture's teardown finds an
    /// ordinary hub.</summary>
    private readonly AsyncSubject<Unit> gateOpen = new();

    private readonly RecordingLoggerProvider recorder = new();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(recorder))
            .AddGraph()
            .AddMeshNodes(new MeshNode(GateNodeType)
            {
                Name = "Include Gate",
                HubConfiguration = config => config.WithInitialization(_ => Observable.Defer(() =>
                {
                    gateActivated.OnNext(Unit.Default);
                    gateActivated.OnCompleted();
                    return gateOpen;
                }))
            });

    [Fact(Timeout = 120_000)]
    public async Task DisposingTheNodeTypeHub_ReleasesTheWatchersInFlightIncludeRead_AndLogsNoFault()
    {
        try
        {
            // ── The include target: an instance of the gated type ────────────────────────────
            await MeshService.CreateNode(new MeshNode("Target", "Gate")
            {
                NodeType = GateNodeType,
                Name = "Target",
                State = MeshNodeState.Active
            }).Should().Within(TestTimeouts.Convergence).Emit();

            // ── The NodeType and its one source, whose @@ include points at the gated target ──
            var typeNode = MeshNode.FromPath(TypePath) with
            {
                Name = "Watched",
                NodeType = MeshNode.NodeTypePath,
                Content = new NodeTypeDefinition
                {
                    Configuration = "config => config.WithContentType<WatchedThing>()"
                },
                State = MeshNodeState.Active
            };
            await MeshService.CreateNode(typeNode).Should().Within(TestTimeouts.Convergence).Emit();
            await MeshService.CreateNode(new MeshNode("model", $"{TypePath}/Source")
            {
                NodeType = "Code",
                Name = "model",
                Content = new CodeConfiguration
                {
                    Code = $"@@{IncludePath}\n\npublic record WatchedThing;",
                    Language = "csharp"
                },
                State = MeshNodeState.Active
            }).Should().Within(TestTimeouts.Convergence).Emit();

            // ── Activate the NodeType hub through the mesh (a routed read), which installs its
            //    watchers; the sources watcher's first pass resolves the @@ closure ───────────
            await Mesh.GetMeshNode(TypePath, TestTimeouts.Convergence)
                .Should().Within(TestTimeouts.Convergence).Emit("the NodeType node must be readable");
            await gateActivated.Should().Within(TestTimeouts.Convergence).Emit(
                "the sources watcher's @@-include read must reach the gated target — that read, "
                + "parked behind the target's never-opening init gate, is the in-flight request the "
                + "rest of this test is about");

            var typeHub = Mesh.GetHostedHub(new Address(TypePath), HostedHubCreation.Never);
            typeHub.Should().NotBeNull("the routed read activated the NodeType's own hub");
            typeHub!.IsShuttingDown.Should().BeFalse("nothing has disposed it yet");

            // ── Dispose the NodeType hub with the include read outstanding ────────────────────
            typeHub.Dispose();
            await typeHub.DisposalCompleted.Take(1).Should().Within(TestTimeouts.Convergence)
                .Emit("the NodeType hub's teardown must complete");

            ((MessageHub)typeHub).QuiescingTimedOut.Should().BeFalse(
                "the sources watcher must release its in-flight @@-include read at the FIRST instant "
                + "of the hub's teardown. A pending callback here means the watcher outlived "
                + "Dispose(): Quiescing waited its whole 2 s budget for a read the watcher should "
                + "have let go of, then errored it — the [FAULT] 2 s after DISPOSE_INVOKED in #3026. "
                + $"Detail: {((MessageHub)typeHub).QuiescingTimeoutDetail}");

            var faults = recorder.Records
                .Where(r => r.Level >= LogLevel.Warning
                            && r.Category == "MeshWeaver.Graph.CompileWatcher"
                            && r.Message.Contains("could not be established", StringComparison.Ordinal))
                .ToArray();
            foreach (var r in recorder.Records)
                Output.WriteLine($"    {r.Level} {r.Category}: {r.Message}"
                                 + (r.Error is null ? "" : $"  << {r.Error.GetType().Name}: {r.Error.Message}"));
            faults.Should().BeEmpty(
                "a read the watcher released is never errored by CancelCallbacks, so the "
                + "'@@-include closure could not be established' warning — the issue's [FAULT] — "
                + "must not appear for a hub that was simply disposed");
        }
        finally
        {
            // Let the gated hub finish initializing so the fixture's teardown meets an ordinary hub.
            gateOpen.OnNext(Unit.Default);
            gateOpen.OnCompleted();
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message, Exception? Error)> records = new();

        public IReadOnlyList<(LogLevel Level, string Category, string Message, Exception? Error)> Records
            => records.ToArray();

        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, records);
        public void Dispose() { }

        private sealed class Recorder(
            string category,
            ConcurrentQueue<(LogLevel, string, string, Exception?)> sink) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => Disposable.Empty;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                sink.Enqueue((logLevel, category, formatter(state, exception), exception));
            }
        }
    }
}
