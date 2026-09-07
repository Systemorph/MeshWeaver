using System;
using System.Collections.Concurrent;
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
/// <para>🚨 It deliberately asserts on the RE-ENQUEUE, not on the write landing: everything it
/// needs has already happened by the time the re-enqueue is logged, and it stays silent about
/// landing, which is the open defect rather than this one.</para>
///
/// <para>🚨 An earlier revision of this comment claimed this test was "deterministic where the
/// sibling is not". THAT WAS FALSE, and measurement said so: on queue-build 34089526911 the
/// sibling passed in the same shard, on the same host, while this test wedged for 90 s and was
/// killed. The cause was this test's own log capture awaiting a live Subject — see
/// <c>CapturingLoggerProvider.FirstMatching</c>. Asserting on a log line rather than on storage
/// buys determinism only if OBSERVING the line cannot perturb what produced it; here it did.</para>
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

        public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
        public void Dispose() { }
        public void Clear() => lines.Clear();

        // 🚨 Enqueue ONLY. There is deliberately no Subject here — see FirstMatching.
        private void Add(string line) => lines.Enqueue(line);

        /// <summary>Waits for a line matching <paramref name="pattern"/> by POLLING the buffer, so
        /// a line logged before this call is seen and one logged after it is not missed.</summary>
        /// <remarks>
        /// 🚨 THIS MUST NOT AWAIT A LIVE SUBJECT, and that is the whole point of the poll. An
        /// earlier version concatenated a replay of the buffer with a <c>Subject&lt;string&gt;</c>
        /// completed from <c>Add</c>. <c>Add</c> runs on WHATEVER THREAD LOGGED — for these lines,
        /// the owner hub's action-block thread — and awaiting an observable resumes its
        /// continuation INLINE on the signalling thread. So everything after the await (the
        /// assertions, the <c>finally</c> that releases the parked merge turn, the method return
        /// and the framework's teardown) ran ON THE HUB'S OWN THREAD while that hub was mid
        /// disposal, and the test wedged in total silence until its Fact timeout killed it.
        ///
        /// <para>It presented as a flake because the two paths differ: when the line was already in
        /// the buffer the REPLAY satisfied it and the continuation resumed on the test's thread
        /// (663 ms locally, green); when the line arrived live, the SUBJECT satisfied it and the
        /// continuation captured the hub thread (90 s, killed, no teardown). Measured on queue-build
        /// 34089526911, where the sibling LateNackReenqueueTest passed in the same shard.</para>
        ///
        /// <para>Polling from <c>Observable.Interval</c> resumes on the scheduler's thread, never on
        /// the hub's, which is what makes the continuation safe. It is also why no Subject remains
        /// in this class — one would be an invitation to reintroduce the await.</para>
        /// </remarks>
        public IObservable<Match> FirstMatching(string pattern, TimeSpan within)
        {
            var re = new Regex(pattern, RegexOptions.Compiled);
            return Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                // 🚨 ENUMERATE the queue; never materialise it. `lines.ToImmutableArray()` goes
                // through ImmutableArray.CreateRange, which reads the sequence's Count, allocates
                // exactly that many slots, and THEN enumerates — two separate reads of a queue the
                // hub's action-block thread is still writing to. One enqueue between them yields
                // more items than slots and ImmutableExtensions.ToArray throws
                // "Value does not fall within the expected range", failing the test on the
                // COLLECTOR rather than on anything it collected. Measured on this PR's CI (run
                // 34102515449, shard 4) while the very line being waited for had already been
                // logged. ConcurrentQueue's own enumerator is a single moment-in-time snapshot, so
                // reading it directly cannot disagree with itself.
                .Select(_ => lines
                    .Select(line => re.Match(line))
                    .FirstOrDefault(m => m.Success))
                .Where(m => m is not null)
                .Select(m => m!)
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
