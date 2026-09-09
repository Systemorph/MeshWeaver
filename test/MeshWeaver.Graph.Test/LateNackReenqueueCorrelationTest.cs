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
/// Pins #3477's correlation chain through the real late-verdict callback. Every attempt
/// registers a fresh request id, while the logical write keeps its original correlation id.
///
/// The original test disposed an owner whose parked merge released on shutdown, then required
/// a late NACK log. That setup also permits a successful ACK, and an armed registry entry does
/// not imply the fast response wait expired: it is registered before the patch is posted.
/// This test observes the response-timeout transition, delivers an explicit late NACK through
/// the registry's existing verdict seam, and checks the child attempt and caller to completion.
/// Owner-disposal verdict delivery itself is covered by LateNackReenqueueTest.
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

        // Prove Debug reaches THIS capture through the exact factory UpdateRemote resolves.
        // The ordinary test-file logger may independently filter Debug; its output is not the
        // evidence that this provider is attached and can observe the transition below.
        Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
        var cacheHub = Mesh.GetHostedHub(new Address("cache", Mesh.Address.Id), HostedHubCreation.Never);
        Assert.NotNull(cacheHub);
        var logger = cacheHub!.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MeshWeaver.Mesh.MeshNodeStreamHandle");
        Assert.True(logger.IsEnabled(LogLevel.Debug));
        var captureProbe = $"correlation-capture-{Guid.NewGuid():N}";
        logger.LogDebug("{CaptureProbe}", captureProbe);
        Assert.True(captured.Contains(captureProbe), "the effective cache logger must reach the capture");

        // Keep the real merge pending until the fast waiter times out and the controlled
        // late verdict has spawned its child. The producer-to-test signal is replayed;
        // the bounded worker release is always written in finally.
        var primary = nodeHub!.GetWorkspace().DataContext
            .GetDataSourceForType(typeof(MeshNode))!
            .GetStreamForPartition(null)!;
        using var gateEntered = new AsyncSubject<Unit>();
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

            // Capture the caller's terminal as well as its value. It must be the chained
            // re-attempt's verdict, not a successful-looking optimistic snapshot.
            var workspace = Mesh.GetWorkspace();
            using var caller = new AsyncSubject<MeshNode>();
            using var writeSub = workspace.GetMeshNodeStream(path)
                .Update(n => n with { Name = "corr-probe" })
                .Subscribe(caller);
            var target = Regex.Escape(path);
            var registration = await captured.FirstMatching(
                    $@"LATE_VERDICT_REGISTERED .*target={target} attempt=0 corr=(?<corr>\S+) requestId=(?<request>\S+)",
                    TestTimeouts.Convergence)
                .Await(ct);
            var corr = registration.Groups["corr"].Value;
            var requestId = registration.Groups["request"].Value;

            // This path has exactly one attempt until we supply the NACK. Unlike ArmedCount,
            // RESPONSE_TIMEOUT proves its fast waiter has handed off to the late callback.
            // The merge remains parked, so neither an ACK nor an actual NACK can win the race.
            await captured.FirstMatching(
                    $@"RESPONSE_TIMEOUT .*target={target} — owner busy;",
                    TestTimeouts.Convergence)
                .Await(ct);
            var registry = Mesh.ServiceProvider.GetRequiredService<LatePatchResponseRegistry>();
            Assert.Contains(requestId, registry.ArmedRequestIds);
            Assert.True(registry.Dispatch(requestId, new PatchDataResponse(false, 0)
            {
                NodeError = new MeshNodeError(MeshNodeErrorCode.OwnerDisposing, path,
                    "Controlled late verdict for correlation inheritance."),
            }), "the exact armed request must consume the controlled late verdict");

            var nack = await captured.FirstMatching(
                    $@"LATE_NACK_REENQUEUE .*target={target} attempt=1 code=OwnerDisposing corr=(?<corr>\S+)",
                    TestTimeouts.Convergence)
                .Await(ct);
            var child = await captured.FirstMatching(
                    $@"LATE_VERDICT_REGISTERED .*target={target} attempt=1 corr=(?<corr>\S+) requestId=(?<request>\S+)",
                    TestTimeouts.Convergence)
                .Await(ct);
            Assert.False(string.IsNullOrWhiteSpace(corr));
            Assert.Equal(corr, nack.Groups["corr"].Value);
            Assert.Equal(corr, child.Groups["corr"].Value);
            var childRequestId = child.Groups["request"].Value;
            Assert.NotEqual(requestId, childRequestId);
            // Registration is logged immediately before insertion. Fence on the child
            // actually being armed, while no accepted merge can yet complete it.
            await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .Where(_ => registry.ArmedRequestIds.Contains(childRequestId))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

            // Both accepted patches use the same idempotent assignment. Releasing the real
            // executor lets the re-attempt finish; its terminal must reach the original caller.
            Volatile.Write(ref releaseGate, 1);
            var terminal = await caller.Timeout(TestTimeouts.Convergence).Await(ct);
            Assert.Equal("corr-probe", terminal.Name);
            var persisted = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
                .Where(n => n is not null && n.Name == "corr-probe")
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            Assert.Equal("corr-probe", persisted!.Name);
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
        public bool Contains(string line) => lines.Contains(line);

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
                // 🚨 ToArray(), never ToImmutableArray(). This queue is written by the hub's
                // action-block thread while the poller reads it, and ImmutableArray.CreateRange
                // takes the ICollection.Count fast path: it reads Count, allocates exactly that
                // many, then copies the enumerator's contents. ConcurrentQueue's enumerator is its
                // own later snapshot, so ONE line enqueued between the two steps overflows the
                // buffer and the poll throws instead of polling again:
                //     System.ArgumentException: Value does not fall within the expected range.
                //       at ImmutableExtensions.ToArray[T](IEnumerable`1 sequence, Int32 count)
                //       at ImmutableArray.CreateRange[T](IEnumerable`1 items)
                // Measured on run 34098887738, shard 4 — and the line that raced is the very
                // LATE_NACK_REENQUEUE warning this test waits for, so the failure lands exactly
                // when the test is about to succeed. ConcurrentQueue.ToArray() has no such split:
                // it snapshots the segments under the queue's own synchronisation and returns a
                // fixed-length array.
                .Select(_ => lines.ToArray()
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
