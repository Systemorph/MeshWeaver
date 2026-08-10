using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// <see cref="RouterTrafficRule"/> is a pure predicate and is pinned as one in
/// <see cref="RouterTrafficRuleTest"/>. What that test cannot see is which VALUES the detector feeds
/// it — and that is where the defect was: <c>MessageHub.ReportRouterTraffic</c> passed
/// <c>Address.Type</c>, the address of the hub RECEIVING the delivery, where the rule's contract is
/// "is the mesh hub an END of this delivery".
///
/// <para>Those two are the same thing only for a delivery that stops where it is handled.
/// <c>HierarchicalRouting</c> routes every hosted hub's non-local delivery UP via
/// <c>parentHub.DeliverMessage(delivery)</c>, and for essentially every hub in the process that
/// parent is the root mesh hub — so <c>mesh.DeliverMessage</c> runs on nearly every cross-address
/// message in the mesh and the detector reported every routing HOP, the router's actual job, as a
/// violation. A monolith repro of merely "validate a token + read a node" emitted five ERROR lines,
/// all of them hops.</para>
///
/// <para>A detector that cries wolf on the mesh's own job is the one that gets muted — taking the
/// real reports (the router-starvation wedge of prod 2026-06-11) with it. So this fixture drives the
/// three shapes through a REAL hub hierarchy and reads the ERROR the detector actually logged:
/// a hop must be SILENT, and both genuine end roles must still FIRE.</para>
/// </summary>
public class RouterTrafficDetectorTest : HubTestBase
{
    /// <summary>client → host: the mesh hub is neither end, it only FORWARDS. Must be silent.</summary>
    private record HopPing : IRequest<Pong>;

    /// <summary>client → mesh: the mesh hub is the TARGET — work on the router. Must fire.</summary>
    private record MeshTargetedRequest : IRequest<Pong>;

    /// <summary>mesh → host: the mesh hub is the SENDER — also work on the router. Must fire.</summary>
    private record MeshSentRequest : IRequest<Pong>;

    private record Pong;

    private readonly RouterTrafficCapture capture = new();

    public RouterTrafficDetectorTest(ITestOutputHelper output) : base(output)
    {
        // Registered AFTER TestBase's ctor has run its ClearProviders(), so this survives alongside
        // the xUnit sink. The detector's whole contract is the ERROR it emits; reading that record
        // is the only way to assert on it without re-implementing the decision in the test.
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(capture));
    }

    /// <summary>
    /// The mesh hub answers <see cref="MeshTargetedRequest"/> itself — i.e. it executes work, the
    /// exact shape the detector exists to name. Deliberate: a report the fixture cannot provoke is
    /// a report nobody can prove still fires.
    /// </summary>
    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => base.ConfigureMesh(conf)
            .WithTypes(typeof(MeshTargetedRequest), typeof(MeshSentRequest), typeof(Pong))
            .WithHandler<MeshTargetedRequest>(RespondWithPong);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(HopPing), typeof(MeshSentRequest), typeof(Pong))
            .WithHandler<HopPing>(RespondWithPong)
            .WithHandler<MeshSentRequest>(RespondWithPong);

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(HopPing), typeof(MeshTargetedRequest), typeof(Pong));

    private static IMessageDelivery RespondWithPong(IMessageHub hub, IMessageDelivery delivery)
    {
        hub.Post(new Pong(), o => o.ResponseFor(delivery));
        return delivery.Processed();
    }

    /// <summary>
    /// 🚨 The regression pin. <c>client → host</c> has the mesh hub at NEITHER end, yet the delivery
    /// is routed THROUGH it (client has no route of its own for <c>host/1</c>, so
    /// <c>HierarchicalRouting</c> hands it to the parent — the mesh). Reporting that is reporting
    /// routing itself, on the single path every cross-address message in the process takes.
    ///
    /// <para>Awaiting the response is the barrier, not a sleep: <c>ReportRouterTraffic</c> runs
    /// synchronously at the top of <c>DeliverMessage</c>, so a reply that has come back proves every
    /// hop's report decision was already made.</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ADeliveryMerelyRoutedThroughTheMeshHub_IsNotReported()
    {
        var client = GetClient();

        var response = await client
            .Observe<Pong>(new HopPing(), o => o.WithTarget(CreateHostAddress()))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
        response.Message.Should().BeOfType<Pong>();

        // Both legs hop through the mesh: the request client→host and the response host→client.
        Reports(nameof(HopPing)).Should().BeEmpty(
            "the mesh hub is neither end of a client→host delivery — it only FORWARDS it. Naming the "
            + "router's own job a violation fires on nearly every message in the process, and a "
            + "detector that noisy gets muted along with its true positives");
        Reports(nameof(Pong)).Should().BeEmpty(
            "the response hops back host→client through the same parent — also pure forwarding");
    }

    /// <summary>
    /// The must-still-fire half, TARGET role: a request addressed AT <c>mesh/{id}</c> and executed
    /// there is the genuine violation — work competing with routing on the router's own action
    /// block (prod 2026-06-11: node CRUD at <c>mesh/&lt;self&gt;</c> starved real
    /// <c>SubscribeRequest</c> traffic into a portal-wide wedge).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnEndTargetedAtTheMeshHub_IsStillReported()
    {
        var client = GetClient();

        var response = await client
            .Observe<Pong>(new MeshTargetedRequest(), o => o.WithTarget(Mesh.Address))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
        response.Message.Should().BeOfType<Pong>();

        var report = Reports(nameof(MeshTargetedRequest)).Should().ContainSingle(
            "a request addressed AT the mesh hub makes the router execute work — the exact "
            + "starvation shape the detector exists to surface, and the one thing it must never "
            + "stop reporting").Subject;
        report.Role.Should().Be("target");
        report.Target.Should().Be(Mesh.Address.ToString(),
            "the line must name the delivery's own target, not whichever hub happened to log it");
    }

    /// <summary>
    /// The must-still-fire half, SENDER role — and the volume driver: the mesh hub POSTING work
    /// (a <c>NodeOperationTarget</c>-shaped call issued from the DI-injected root hub) is the
    /// violation that actually shows up in production traffic.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TheMeshHubAsSender_IsStillReported()
    {
        GetHost();

        var response = await Mesh
            .Observe<Pong>(new MeshSentRequest(), o => o.WithTarget(CreateHostAddress()))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
        response.Message.Should().BeOfType<Pong>();

        var report = Reports(nameof(MeshSentRequest)).Should().ContainSingle(
            "the mesh hub posting work is the genuine NodeOperationTarget violation — the sender "
            + "role must survive any narrowing of the target role").Subject;
        report.Role.Should().Be("sender");
        report.Sender.Should().Be(Mesh.Address.ToString());
        report.Target.Should().Be(CreateHostAddress().ToString(),
            "the delivery is addressed to the host hub; the line must say so");
    }

    private RouterTrafficRecord[] Reports(string messageType)
    {
        var all = capture.Records;
        foreach (var record in all)
            Output.WriteLine($"ROUTER_TRAFFIC captured: {record}");
        return all.Where(r => r.MessageType == messageType).ToArray();
    }

    private sealed record RouterTrafficRecord(string MessageType, string Role, string Sender, string Target);

    /// <summary>
    /// Reads the detector's own ERROR out of the logging pipeline. Structured state, not the
    /// formatted string: the assertions then pin the VALUES the detector chose (role, sender,
    /// target) rather than the prose around them, which is free to be reworded.
    /// </summary>
    private sealed class RouterTrafficCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<RouterTrafficRecord> records = new();

        internal RouterTrafficRecord[] Records => records.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(records);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<RouterTrafficRecord> sink) : ILogger
        {
            private sealed class NullScope : IDisposable
            {
                internal static readonly NullScope Instance = new();
                public void Dispose() { }
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Error || state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                    return;
                if (!formatter(state, exception).StartsWith("ROUTER_TRAFFIC:", StringComparison.Ordinal))
                    return;

                sink.Enqueue(new RouterTrafficRecord(
                    Value(values, "MessageType"),
                    Value(values, "Role"),
                    Value(values, "Sender"),
                    Value(values, "Target")));
            }

            private static string Value(IReadOnlyList<KeyValuePair<string, object?>> values, string key)
            {
                foreach (var pair in values)
                    if (pair.Key == key)
                        return pair.Value?.ToString() ?? "(null)";
                return "(absent)";
            }
        }
    }
}
