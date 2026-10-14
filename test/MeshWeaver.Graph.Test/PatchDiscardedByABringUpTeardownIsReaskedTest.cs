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
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Systemorph/MeshWeaver#6733 — a cross-hub write (<see cref="PatchDataRequest"/>) parked behind
/// the initialization gates of an owner that is torn down before its bring-up finishes, the shape
/// an Orleans deactivation produces during a roll (<c>DirectoryFailure</c>: the directory owner's
/// silo is stopping, and <c>MessageHubGrain.OnDeactivateAsync</c> disposes the hub directly).
///
/// <para><b>What the incident asked.</b> Is that write silently lost? The owner's discard site
/// (<c>MessageService</c>, event 7301) answers the parked delivery with a transient
/// <see cref="ErrorType.ShuttingDown"/> NACK, and the only producer of a node patch —
/// <c>UpdateRemote</c> behind <c>GetMeshNodeStream(path).Update(...)</c> — re-enqueues the
/// ORIGINAL update against the address's next activation on exactly that answer
/// (<c>OWNER_SHUTTING_DOWN_REENQUEUE</c> / <c>LATE_SHUTTING_DOWN_REENQUEUE</c>). So nothing is lost:
/// the caller completes as a success and the store carries the write. This test pins both halves
/// end to end.</para>
///
/// <para><b>What was wrong.</b> The producer never DECLARED that it re-asks. The discard site
/// classifies a delivery as teardown-normal only when its type carries
/// <c>[ReaskedOnShutdown]</c> or the request carries <see cref="PostOptions.ReasksOnShutdown"/>;
/// the patch had neither, so a routine roll-time discard of a write that was about to land on the
/// next activation was reported at Error as stranded work and filed as an incident. The producer
/// now stamps <see cref="PostOptions.ReaskingOnShutdown"/> on its own request, the per-delivery
/// claim the node point read already makes (#5718).</para>
///
/// <para><b>Controls.</b> The discard is asserted POSITIVELY at Debug (it must still be reported,
/// with the gate that held it and the teardown that discarded it), so "reclassified" cannot pass as
/// "the site stopped reporting". The undeclared-request control — the same teardown over a request
/// whose producer does not re-ask still reports at Error — is
/// <c>DeferredDeliveryNackedOnDisposeTest.DeferredRequest_IsNacked_WhenHubIsDisposedBeforeItsGateOpens</c>.</para>
/// </summary>
public class PatchDiscardedByABringUpTeardownIsReaskedTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string ProbeNodeType = "PatchBringUpTeardownProbe";
    private const string NodeId = "patch-bringup-node";
    private const string NodePath = $"{TestPartition}/{NodeId}";
    private const string TeardownBy = "Orleans deactivating grain " + NodePath;
    private const string HoldGate = "test-holds-patches";

    private readonly DiscardLog log = new();

    /// <summary>1 while the NEXT activation of the probe node is to hold patches; consumed by it.</summary>
    private int holdNextActivation;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(ProbeNodeType)
            {
                Name = "Patch bring-up teardown probe",
                HubConfiguration = HoldPatchesOnTheArmedActivation,
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
    /// The armed activation — exactly ONE, consumed here — gets an initialization gate that never
    /// opens and lets every message through but a <see cref="PatchDataRequest"/>. The writer's
    /// subscribe is served and its mirror hydrates, while its patch is PARKED behind the young
    /// activation's gates: the production shape, a patch that reached an activation whose bring-up
    /// had not finished. The activation the re-asked write brings up afterwards is not armed.
    /// </summary>
    private MessageHubConfiguration HoldPatchesOnTheArmedActivation(MessageHubConfiguration config)
        => string.Equals(config.Address.ToString(), NodePath, StringComparison.Ordinal)
           && Interlocked.Exchange(ref holdNextActivation, 0) == 1
            ? config.WithInitializationGate(HoldGate, d => d.Message is not PatchDataRequest)
            : config;

    [Fact(Timeout = 600_000)]
    public async Task APatchDiscardedByABringUpTeardown_LandsOnTheNextActivation_AndIsTeardownNormal()
    {
        var ct = TestContext.Current.CancellationToken;
        await NodeFactory.CreateNode(
                new MeshNode(NodeId, TestPartition) { Name = "initial", NodeType = ProbeNodeType })
            .Should().Emit();
        var workspace = Mesh.GetWorkspace();
        await workspace.GetMeshNodeStream(NodePath).Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit("the node is readable");

        // End the current activation, and arm the hold for the NEXT one: the activation a roll
        // brings up, whose bring-up has not finished when its silo leaves.
        var first = Mesh.GetHostedHub(new Address(NodePath), HostedHubCreation.Never);
        first.Should().NotBeNull();
        Volatile.Write(ref holdNextActivation, 1);
        first!.Dispose();
        await first.DisposalCompleted.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var marker = $"bringup-{Guid.NewGuid():N}"[..24];
        MeshNode? callerTerminal = null;
        Exception? callerError = null;
        using var writeSub = workspace.GetMeshNodeStream(NodePath)
            .Update(n => n with { Name = marker })
            .Subscribe(
                n => Volatile.Write(ref callerTerminal, n),
                ex => Volatile.Write(ref callerError, ex));

        // Fence: THIS write's patch is parked behind the young activation's gates. Observed on the
        // request-fate ledger, never assumed — a patch still in transit is a different path.
        var registry = Mesh.ServiceProvider.GetRequiredService<LatePatchResponseRegistry>();
        var patchId = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => registry.ArmedRequestIds)
            .Where(id =>
            {
                var trail = Mesh.DescribeRequestFate(id);
                return trail.Contains("DEFERRED gates=[", StringComparison.Ordinal)
                       && trail.Contains(HoldGate, StringComparison.Ordinal);
            })
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine($"[fence] patch {patchId} is deferred: {Mesh.DescribeRequestFate(patchId)}");

        // The production initiator: a DIRECT dispose with a stated cause, which is what
        // MessageHubGrain does when Orleans deactivates the grain.
        var young = Mesh.GetHostedHub(new Address(NodePath), HostedHubCreation.Never);
        young.Should().NotBeNull();
        ((MessageHub)young!).NoteDirectDisposalBy(TeardownBy,
            "DirectoryFailure — the silo holding this activation's directory registration is shutting down");
        young.Dispose();

        var settled = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .Where(_ => Volatile.Read(ref callerTerminal) is not null
                || Volatile.Read(ref callerError) is not null)
            .Select(_ => true)
            .Take(1)
            .Timeout(TestTimeouts.WriteConvergence, Observable.Return(false))
            .FirstAsync().Await(ct);
        settled.Should().BeTrue("the write must reach a terminal; UpdateRemote bounds every attempt");
        Volatile.Read(ref callerError).Should().BeNull(
            "the producer re-asks the next activation on the ShuttingDown NACK, so the write lands");
        Volatile.Read(ref callerTerminal)!.Name.Should().Be(marker);

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var persisted = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => storage.Read(NodePath, Mesh.JsonSerializerOptions))
            .Where(n => n is not null && n.Name == marker)
            .Take(1)
            .Timeout(TestTimeouts.Quick, Observable.Return<MeshNode?>(null))
            .FirstAsync().Await(ct);
        persisted.Should().NotBeNull(
            "the discarded patch was re-asked and applied on the next activation — not lost");

        var patchDiscards = log.Lines
            .Where(l => l.Message.Contains(nameof(PatchDataRequest), StringComparison.Ordinal)
                        && l.Message.Contains(TeardownBy, StringComparison.Ordinal))
            .ToArray();
        patchDiscards.Should().Contain(l => l.Level == LogLevel.Debug
                                            && l.Message.Contains(HoldGate, StringComparison.Ordinal),
            "the discard is still REPORTED, with the gate that held it and the teardown that "
            + "discarded it — a change that stopped reporting would be a silenced fault, not a "
            + "classification");
        patchDiscards.Should().NotContain(l => l.Level >= LogLevel.Error,
            "the patch's producer re-asks the next activation on the ShuttingDown answer and the "
            + "write landed there, so an Error alleging stranded work has no victim (#6733)");
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
