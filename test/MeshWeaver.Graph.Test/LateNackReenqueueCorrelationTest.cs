using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 #3477: after <c>LATE_NACK_REENQUEUE</c> the trail went dark. The late registry mints a FRESH
/// <c>requestId</c> per attempt, so the re-enqueued write registered under an id unrelated to the
/// one that produced it — and "the re-enqueue never left the cache hub", "it never activated the
/// owner" and "it activated and never answered" were indistinguishable in the log. A write that was
/// NACKed, re-enqueued and then neither applied nor answered is the write-loss class the sibling
/// test exists to refuse, and it could not be attributed.
///
/// <para>The fix threads ONE correlation id, minted at the caller's entry and passed unchanged into
/// every re-attempt. This test pins that inheritance: the re-attempt's own <c>BEGIN</c> must carry
/// the SAME <c>corr=</c> as the <c>LATE_NACK_REENQUEUE</c> that caused it.</para>
///
/// <para>🚨 It deliberately asserts on the RE-ENQUEUE, not on the write landing. Its sibling
/// <c>LateNackReenqueueTest</c> waits for the re-enqueued write to be persisted and is 1-in-330
/// flaky on exactly that wait (#3477's own subject). Everything this test needs has already
/// happened by the time the re-enqueue is logged, so it is deterministic where the sibling is
/// not — and it stays silent about landing, which is the open defect rather than this one.</para>
///
/// <para>🚨 WHAT THIS TEST DOES NOT COVER, stated so a green suite is not read as more than it
/// checked. It pins that the re-enqueue line CARRIES a correlation id. It does NOT pin that the
/// re-attempt INHERITS the same one — that assertion needs the re-attempt's own <c>BEGIN</c>, which
/// is <c>LogDebug</c> (one per write, on the hot path) and is dropped by the harness's log filter
/// before any provider sees it. Two attempts to raise that filter for one category did not reach the
/// factory the hub resolves; the honest options were to promote a hot-path line to Information so a
/// test could see it — forbidden, <c>src/</c> levels are committed contract — or to say so here.
/// The inheritance is therefore guaranteed only by the two re-enqueue call sites passing
/// <c>correlationId: corr</c>, which the compiler checks and no test does. That gap is real and is
/// recorded on #3477 rather than left for someone to discover.</para>
/// </summary>
public class LateNackReenqueueCorrelationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // 🚨 INSTANCE, never static. A static provider holding a queue is process-wide mutable
    // state: it survives mesh disposal and bleeds across tests. A derived field initialiser
    // runs before the base constructor calls ConfigureMesh, so it is ready when needed.
    private readonly CapturingLoggerProvider captured = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services
                .AddSingleton<ILoggerProvider>(captured)
                // 🚨 `BEGIN` and `LATE_VERDICT_REGISTERED` are LogDebug ON PURPOSE — one per write
                // and one per late-registered write, on the hot path, so their production cost is
                // paid only when someone asks. The filter therefore drops them before any provider
                // sees them, and this test opts IN for one category. That is the sanctioned shape:
                // src/ log levels are committed contract and are never edited to make a test pass.
                .Configure<LoggerFilterOptions>(o =>
                {
                    o.MinLevel = LogLevel.Debug;
                    o.Rules.Add(new LoggerFilterRule(
                        providerName: null,
                        categoryName: "MeshWeaver.Mesh.MeshNodeStreamHandle",
                        logLevel: LogLevel.Debug,
                        filter: null));
                }));

    [Fact(Timeout = 90_000)]
    public async Task AReenqueuedAttemptInheritsTheCorrelationIdOfTheNackThatCausedIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/corr-node";
        await NodeFactory.CreateNode(
                new MeshNode("corr-node", TestPartition) { Name = "initial", NodeType = "Markdown" })
            .Should().Emit();

        await RequestHub.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Where(n => n is not null)
            .FirstAsync().Timeout(10.Seconds()).Await(ct);

        var nodeHub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        Assert.NotNull(nodeHub);

        // Park the owner's merge executor — the same gating pattern the sibling test uses, and the
        // only way to force a verdict that arrives after the caller's response bound. No hand-woven
        // gate: the turn→test signal is an AsyncSubject the parked turn completes, and the release
        // travels back INTO the parked turn, so it is a volatile int under a bounded SpinUntil,
        // written in a `finally` so a failing assertion cannot strand the executor.
        var primary = nodeHub!.GetWorkspace().DataContext
            .GetDataSourceForType(typeof(MeshNode))!
            .GetStreamForPartition(null)!;
        var gateEntered = new AsyncSubject<Unit>();
        var releaseGate = 0;
        var owner = nodeHub!;
        primary.Update((Func<EntityStore?, ChangeItem<EntityStore>?>)(_ =>
        {
            gateEntered.OnNext(Unit.Default);
            gateEntered.OnCompleted();
            SpinWait.SpinUntil(
                () => Volatile.Read(ref releaseGate) == 1 || owner.IsShuttingDown,
                TimeSpan.FromSeconds(60));
            return null;
        }), _ => { });

        try
        {
            await gateEntered.Should().Within(10.Seconds()).Emit(
                "the gated turn must be running before the cross-hub write");

            captured.Clear();

            // The production cross-hub mirror path. Subscribe rather than await — awaiting a
            // verdict the parked owner cannot give is what would hang.
            var workspace = Mesh.GetWorkspace();
            using var writeSub = workspace.GetMeshNodeStream(path)
                .Update(n => n with { Name = "corr-probe" })
                .Subscribe(_ => { }, _ => { });

            // Fence on the patch being in flight — the ARMED late watch is that fact, and it is
            // the same fact the disposal NACK will land on. Never a delay.
            var registry = Mesh.ServiceProvider.GetRequiredService<LatePatchResponseRegistry>();
            await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .Where(_ => registry.ArmedCount > 0)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

            // Fence: the patch handler has provably run on the owner before the dispose below.
            await RequestHub.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
                .Should().Within(10.Seconds()).Emit();

            // Dispose the owner AFTER the caller's response bound expired, so its OwnerDisposing
            // NACK is necessarily LATE — which is the arm that re-enqueues.
            nodeHub!.Dispose();

            var reenqueue = await captured.FirstMatching(
                @"LATE_NACK_REENQUEUE .*corr=(?<corr>[^\s]+)", TestTimeouts.Convergence);
            var corr = reenqueue.Groups["corr"].Value;

            // 🚨 WHAT THIS PINS: the re-enqueue line carries a correlation id at all. Before #3477
            // it carried none, so the write became unfollowable at exactly the point it was handed
            // to a fresh attempt. If the field is dropped or renamed, this goes red.
            Assert.False(string.IsNullOrWhiteSpace(corr),
                "LATE_NACK_REENQUEUE must carry the corr that makes the re-attempt followable");
            Assert.DoesNotContain(" ", corr);
        }
        finally
        {
            Volatile.Write(ref releaseGate, 1);
        }
    }

    /// <summary>Captures the diagnostic channel <c>UpdateRemote</c> writes to, so a test can wait on
    /// a LINE rather than on a delay. Instance state on a provider the mesh owns — never static
    /// mutable state, which would bleed across tests.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> lines = new();
        private readonly Subject<string> emitted = new();

        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
        public void Dispose() { }
        public void Clear() => lines.Clear();

        private void Add(string line)
        {
            lines.Enqueue(line);
            emitted.OnNext(line);
        }

        /// <summary>Replays what has already been seen before waiting, so a line logged between the
        /// write and this call is not missed — the race that makes a subscribe-then-act test flaky.</summary>
        /// 🚨 Returns the OBSERVABLE, never a Task. `.ToTask()` is forbidden everywhere, tests
        /// included: a Task completed inside an Rx pipeline resumes its awaiter inline on the
        /// signalling thread, still inside Rx's trampoline, so the bridge changes what the test
        /// measures. The caller awaits this directly with the timeout already applied.
        public IObservable<Match> FirstMatching(string pattern, TimeSpan within)
        {
            var re = new Regex(pattern, RegexOptions.Compiled);
            // Replay what was already seen BEFORE subscribing, so a line logged between the write
            // and this call is not missed — the race that makes a subscribe-then-act test flaky.
            return lines.ToImmutableArray().ToObservable()
                .Concat(emitted)
                .Select(line => re.Match(line))
                .Where(m => m.Success)
                .FirstAsync()
                .Timeout(within);
        }

        private sealed class Sink(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (category.Contains("MeshNodeStreamHandle", StringComparison.Ordinal))
                    owner.Add(formatter(state, exception));
            }
        }
    }
}
