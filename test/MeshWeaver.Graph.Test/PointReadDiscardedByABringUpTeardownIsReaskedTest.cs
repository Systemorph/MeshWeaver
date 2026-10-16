using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Systemorph/MeshWeaver#6717 (item 2) — the PRODUCTION call site of the per-delivery
/// <see cref="PostOptions.ReasksOnShutdown"/> claim: the node point read,
/// <see cref="MeshNodeStreamExtensions.GetMeshNodeOutcome"/> (#5718/#6688).
///
/// <para><b>What it pins.</b> The point read rides a <see cref="ErrorType.ShuttingDown"/> answer
/// out by re-issuing its <see cref="GetDataRequest"/> against the address's next activation, and
/// it DECLARES that on its request so the owner's discard site (<c>MessageService</c>, event 7301)
/// classifies a bring-up teardown that discards it as teardown-normal (Debug), not as stranded work
/// (Error). Both deliveries are covered: the INITIAL request and the RE-PROBE issued from the
/// ShuttingDown NACK's callback. Each is parked behind the gates of an activation whose bring-up
/// never finishes, and each activation is then torn down the way an Orleans deactivation does it
/// (a direct dispose with a stated cause). The read must still settle <see cref="NodeReadStatus.Present"/>
/// on the third activation.</para>
///
/// <para><b>Controls.</b> Each discard is asserted POSITIVELY at Debug, naming the gate that held
/// it, so "reclassified" cannot pass as "the site stopped reporting". The undeclared-request
/// control — the same teardown over a request whose producer does not re-ask still reports at
/// Error — is <c>DeferredDeliveryNackedOnDisposeTest.DeferredRequest_IsNacked_WhenHubIsDisposedBeforeItsGateOpens</c>.
/// The write-side twin of this test is <see cref="PatchDiscardedByABringUpTeardownIsReaskedTest"/>.</para>
/// </summary>
public class PointReadDiscardedByABringUpTeardownIsReaskedTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string ProbeNodeType = "PointReadBringUpTeardownProbe";
    private const string NodeId = "pointread-bringup-node";
    private const string NodePath = $"{TestPartition}/{NodeId}";
    private const string TeardownBy = "Orleans deactivating grain " + NodePath;
    private const string HoldGate = "test-holds-point-reads";

    private readonly DiscardLog log = new();

    /// <summary>How many of the NEXT activations of the probe node hold point reads; each consumes one.</summary>
    private int armedActivations;

    /// <summary>Ordinal of the most recently ARMED activation of the probe node (1 = first armed).</summary>
    private int armedOrdinal;

    /// <summary>The point-read deliveries the held activations parked, keyed by delivery id.</summary>
    private readonly ConcurrentDictionary<string, (int Activation, IMessageDelivery Delivery)> parked = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(ProbeNodeType)
            {
                Name = "Point-read bring-up teardown probe",
                HubConfiguration = HoldPointReadsOnTheArmedActivations,
            })
            .ConfigureServices(services => services
                .AddSingleton<ILoggerProvider>(log)
                // Event 7301 is asserted at BOTH levels, so its Debug form must reach the capture —
                // otherwise "the Error is gone" and "the line vanished" read identically.
                .Configure<LoggerFilterOptions>(o => o.Rules.Add(new LoggerFilterRule(
                    providerName: null,
                    categoryName: "MeshWeaver.Messaging.MessageService",
                    logLevel: LogLevel.Debug,
                    filter: null))));

    /// <summary>
    /// An armed activation gets an initialization gate that never opens and lets every message
    /// through but a point read (<see cref="GetDataRequest"/> for the <see cref="MeshNodeReference"/>),
    /// which it PARKS — the production shape, a read that reached an activation whose bring-up had
    /// not finished. The predicate is where the hub decides to defer, so it records the delivery
    /// exactly as it arrived (properties included). Unarmed activations are left untouched.
    /// </summary>
    private MessageHubConfiguration HoldPointReadsOnTheArmedActivations(MessageHubConfiguration config)
    {
        if (!string.Equals(config.Address.ToString(), NodePath, StringComparison.Ordinal))
            return config;
        int remaining;
        do
        {
            remaining = Volatile.Read(ref armedActivations);
            if (remaining <= 0)
                return config;
        } while (Interlocked.CompareExchange(ref armedActivations, remaining - 1, remaining) != remaining);
        var ordinal = Interlocked.Increment(ref armedOrdinal);
        return config.WithInitializationGate(HoldGate, d =>
        {
            if (d.Message is not GetDataRequest { Reference: MeshNodeReference })
                return true;
            parked.TryAdd(d.Id, (ordinal, d));
            return false;
        });
    }

    [Fact(Timeout = 600_000)]
    public async Task APointReadDiscardedTwiceByBringUpTeardowns_DeclaresItReasks_IsTeardownNormal_AndSettlesPresent()
    {
        var ct = TestContext.Current.CancellationToken;
        await NodeFactory.CreateNode(
                new MeshNode(NodeId, TestPartition) { Name = "point-read", NodeType = ProbeNodeType })
            .Should().Emit();
        await Mesh.GetWorkspace().GetMeshNodeStream(NodePath).Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit("the node is readable");

        // End the current activation and arm the NEXT TWO: the initial request is parked on the
        // first, its re-probe on the second, and the third serves the read.
        var first = Mesh.GetHostedHub(new Address(NodePath), HostedHubCreation.Never);
        first.Should().NotBeNull();
        Volatile.Write(ref armedActivations, 2);
        first!.Dispose();
        await first.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        NodeReadOutcome? outcome = null;
        Exception? readError = null;
        using var readSub = Mesh.GetMeshNodeOutcome(NodePath, TestTimeouts.WriteConvergence)
            .Subscribe(o => Volatile.Write(ref outcome, o), ex => Volatile.Write(ref readError, ex));

        var initial = await TearDownTheActivationHolding(parkedOrdinal: 1, ct);
        var reProbe = await TearDownTheActivationHolding(parkedOrdinal: 2, ct);

        initial.Id.Should().NotBe(reProbe.Id, "the re-probe is a NEW delivery against the next activation");
        initial.Properties.ContainsKey(PostOptions.ReasksOnShutdown).Should().BeTrue(
            "GetMeshNodeOutcome's INITIAL request declares that it re-asks on ShuttingDown (#5718)");
        reProbe.Properties.ContainsKey(PostOptions.ReasksOnShutdown).Should().BeTrue(
            "the RE-PROBE issued from the ShuttingDown NACK's callback is the same read and makes "
            + "the same claim — it re-asks again if this activation also goes down");

        var settled = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .Where(_ => Volatile.Read(ref outcome) is not null || Volatile.Read(ref readError) is not null)
            .Select(_ => true)
            .Take(1)
            .Timeout(TestTimeouts.WriteConvergence, Observable.Return(false))
            .FirstAsync().Await(ct);
        settled.Should().BeTrue("the read must reach a terminal within its own budget");
        Volatile.Read(ref readError).Should().BeNull();
        Volatile.Read(ref outcome)!.Status.Should().Be(NodeReadStatus.Present,
            "both discarded probes were re-asked, and the third activation served the read");
        Volatile.Read(ref outcome)!.Node!.Name.Should().Be("point-read");

        foreach (var delivery in new[] { initial, reProbe })
        {
            var discards = log.Lines
                .Where(l => l.Message.Contains($"id={delivery.Id}", StringComparison.Ordinal))
                .ToArray();
            discards.Should().Contain(l => l.Level == LogLevel.Debug
                                           && l.Message.Contains(TeardownBy, StringComparison.Ordinal)
                                           && l.Message.Contains(HoldGate, StringComparison.Ordinal),
                $"the discard of {delivery.Id} is still REPORTED, with the gate that held it and the "
                + "teardown that discarded it — a change that stopped reporting would be a silenced "
                + "fault, not a classification");
            discards.Should().NotContain(l => l.Level >= LogLevel.Error,
                $"the point read re-asks the next activation on the ShuttingDown answer, so the "
                + $"discard of {delivery.Id} strands nothing (#6717)");
        }
    }

    /// <summary>
    /// Waits until activation <paramref name="parkedOrdinal"/> has PARKED a point read behind
    /// <see cref="HoldGate"/> (observed at the gate predicate, the hub's own deferral decision —
    /// and re-proved afterwards by the discard line naming that gate), then disposes
    /// that activation the way an Orleans deactivation does and waits for the dispose to finish.
    /// Returns the parked delivery as the owner received it.
    /// </summary>
    private async Task<IMessageDelivery> TearDownTheActivationHolding(int parkedOrdinal, CancellationToken ct)
    {
        var delivery = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => parked.Values.Where(p => p.Activation == parkedOrdinal).Select(p => p.Delivery))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"[fence] activation {parkedOrdinal} parked {delivery.Id}");

        var holder = Mesh.GetHostedHub(new Address(NodePath), HostedHubCreation.Never);
        holder.Should().NotBeNull();
        ((MessageHub)holder!).NoteDirectDisposalBy(TeardownBy,
            "DirectoryFailure — the silo holding this activation's directory registration is shutting down");
        holder.Dispose();
        await holder.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        return delivery;
    }

    private sealed class DiscardLog : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(Lines);
        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<(LogLevel, string)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;
            public void Log<TState>(LogLevel level, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id == 7301)
                    lines.Enqueue((level, formatter(state, exception)));
            }
        }
    }
}
