using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
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
/// 🚨 <b>The discriminating instrument for
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/1140">#1140</see></b> — the production
/// <c>ROUTER_TRAFFIC</c> pair that has fired 41,087 times on <c>memex</c> between 2026-08-10 and
/// 2026-09-12, every recent sample naming the log-incident ingest:
///
/// <code>
/// RawJson has the mesh hub as sender (sender: mesh/6CuC…, target: Admin/_LogIncident/cb33ad…)
/// CreateNodeResponse has the mesh hub as target (sender: Admin/_LogIncident/cb33ad…, target: mesh/6CuC…)
/// </code>
///
/// <para><b>What the production samples cannot say.</b> The detector fires on the RECEIVING hub, so
/// the line carries the delivery's two ends and no call site. Two candidates produce exactly that
/// pair and the log cannot separate them: <c>MeshService.CreateNode</c> issuing on
/// <c>NodeOperationIssuingHub()</c> — which is <c>NodeOperationExecutionHub() ?? hub</c>, i.e. the
/// ROOT hub whenever the execution hub cannot be materialised — or the post-creation
/// <c>DataChangeRequest</c> the create path posts for a handler's additional nodes. This fixture
/// drives the FIRST candidate through a real mesh and reads what the detector actually logged, so
/// the answer is measured rather than picked.</para>
///
/// <para><b>The shape is production's, not a stand-in.</b> <c>LogIncidentIngestService</c> is a
/// mesh-singleton that takes the DI-injected <see cref="IMessageHub"/> — which in the mesh's root
/// container IS the router — and calls
/// <c>hub.ServiceProvider.GetRequiredService&lt;IMeshService&gt;().CreateNode(node)</c>. That is
/// exactly what <see cref="ACreateIssuedFromTheRootMeshHub_NeverPutsTheRouterOnEitherEnd"/> does,
/// and it is the shape every mesh-singleton consumer of the seam has (the plugin-catalog boot
/// services, the content importers, the one-shot node reads).</para>
///
/// <para><b>Scope.</b> This covers the request/response exchange — candidate one. The post-creation
/// <c>DataChangeRequest</c> fires only for a post-creation handler that returns ADDITIONAL nodes,
/// which the probe's type has none of, so a green here narrows #1140 to that second candidate
/// rather than clearing the create path wholesale.</para>
///
/// <para><b>Both detector sites are read.</b> <c>MessageHub</c> reports the violation twice over:
/// once on the RECEIVING hub (<c>ROUTER_TRAFFIC:</c>, the historical line, which carries the two
/// addresses and no call site) and once at the ORIGIN (<c>ROUTER_TRAFFIC ORIGIN:</c>, which carries
/// the posting stack). The measurement requires silence from BOTH, so a violating post is caught
/// even where the receiving hub's own report is muted.</para>
///
/// <para><b>The negative control is not optional.</b> A green above is evidence only if this
/// fixture's capture CAN record a violation — see
/// <see cref="TheCapture_RecordsAViolation_WhenTheRouterGenuinelyPostsWork"/>, which makes the
/// router an end on purpose and requires BOTH lines, the origin one naming this test's own frame.
/// Without it, "no ROUTER_TRAFFIC" and "the logger provider was never wired into this mesh" are the
/// same reading.</para>
/// </summary>
public class RouterTrafficOnNodeCreateFromTheRootHubTest : MonolithMeshTestBase
{
    /// <summary>The control's probe: a message with no meaning beyond being WORK the router posts.</summary>
    private record RouterOriginProbe;

    private readonly RouterTrafficCapture _capture = new();

    /// <summary>Producer → test: the client hub completes this when the probe reaches its handler.</summary>
    private readonly System.Reactive.Subjects.AsyncSubject<System.Reactive.Unit> _probeArrived = new();

    public RouterTrafficOnNodeCreateFromTheRootHubTest(ITestOutputHelper output) : base(output)
    {
        // Registered AFTER the base constructor's ClearProviders(), so it survives alongside the
        // xUnit sink. The detector's whole contract is the ERROR it emits; reading that record is
        // the only way to assert on it without re-implementing the decision here.
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(_capture));
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureHub(c => c.WithTypes(typeof(RouterOriginProbe)));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(RouterOriginProbe))
            .WithHandler<RouterOriginProbe>((_, delivery) =>
            {
                _probeArrived.OnNext(System.Reactive.Unit.Default);
                _probeArrived.OnCompleted();
                return delivery.Processed();
            });

    /// <summary>
    /// 🚨 THE MEASUREMENT. A node create issued the way every mesh-singleton service issues one —
    /// from the DI-injected root hub — must leave the router off BOTH ends of the exchange.
    ///
    /// <para>Red ⇒ <c>NodeOperationIssuingHub</c> is the live path behind #1140's production lines,
    /// and the captured record names the role it failed in. Green ⇒ the seam holds for the create
    /// exchange and the remaining candidate is the post-creation <c>DataChangeRequest</c>.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACreateIssuedFromTheRootMeshHub_NeverPutsTheRouterOnEitherEnd()
    {
        // The production shape verbatim: the service holds the root hub and resolves IMeshService
        // from ITS provider, so MeshService's injected IMessageHub is the router.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var created = await meshService
            .CreateNode(new MeshNode("RouterTrafficIngestProbe", TestPartition)
            {
                Name = "Router Traffic Ingest Probe",
                NodeType = "Markdown",
            })
            .FirstAsync()
            .Await(TestContext.Current.CancellationToken);

        created.Path.Should().Be($"{TestPartition}/RouterTrafficIngestProbe",
            "the create must actually have happened — a create that never ran emits no traffic at "
            + "all, which would make the assertion below vacuous");

        // Both halves of the production pair, asserted separately so a red says WHICH one fired.
        DumpReports();
        Reports().Where(r => r.Role.Contains("sender", StringComparison.Ordinal)).Should().BeEmpty(
            "#1140's first production line is the request reaching the node's own hub stamped "
            + "`sender: mesh/{id}`. A create issued from the DI root hub must leave from the "
            + "off-router execution hub (NodeOperationIssuingHub) instead");
        Reports().Where(r => r.Role.Contains("target", StringComparison.Ordinal)).Should().BeEmpty(
            "#1140's second production line is `CreateNodeResponse … target: mesh/{id}` — the reply "
            + "addressed straight back at the router, which follows from the request's sender and "
            + "makes the router run the continuation of someone else's write");
        Origins().Should().BeEmpty(
            "the ORIGIN detector must be silent too — it fires where the delivery is created, so it "
            + "sees a violating post even when the receiving hub's own report is muted");
    }

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL, and it is not optional. The measurement above reads "no records" as
    /// "the seam held" — a reading that is valid only if this fixture's capture provider is really
    /// in the mesh's logging pipeline and the detector is really armed on these hubs. So make the
    /// router an END on purpose and require the line.
    ///
    /// <para>A plain <c>Post</c> to a client hub, deliberately: it is the "sender" role — the same
    /// role #1140's first line reports — and it needs no node CRUD, so a failure here cannot be
    /// confused with a failure of the create path. The arrival subject is the barrier rather than a
    /// sleep: <c>ReportRouterTraffic</c> runs synchronously at the top of <c>DeliverMessage</c>,
    /// strictly before the delivery reaches the handler that completes it; the ORIGIN report is
    /// made synchronously inside <c>Post</c>, so it is already recorded when that method returns.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheCapture_RecordsAViolation_WhenTheRouterGenuinelyPostsWork()
    {
        var client = GetClient();

        // 🚨 Posted FROM the router on purpose — producing the violating shape IS this test's
        // subject. `Post`, not `Observe`: no response is wanted, and the router-as-request-origin
        // ratchet (#2423) is about request/response exchanges a test could have issued elsewhere.
        Mesh.Post(new RouterOriginProbe(), o => o.WithTarget(client.Address));

        await _probeArrived.Should(TestContext.Current.CancellationToken).Within(TestTimeouts.Convergence)
            .Emit("the probe must actually reach the client hub, or nothing was delivered and this "
                + "control proves nothing");

        DumpReports();
        // 🚨 Asserted on ROLE and ENDS, never on the message type. The client hub is reached over a
        // serializing route, so the delivery arrives packed — the detector reads `RawJson`, which is
        // precisely the type #1140's production lines carry. Keying this control on
        // `nameof(RouterOriginProbe)` found 0 records and would have read as "the capture is dead".
        var report = Reports().Should().ContainSingle(
            "the capture must be able to record a genuine violation, or the measurement's green is "
            + "an instrument that cannot fail rather than a seam that holds").Subject;
        report.Role.Should().Be("sender");
        report.Sender.Should().Be(Mesh.Address.ToString(),
            "the recorded line must name the delivery's own sender — the router — not whichever hub "
            + "happened to log it");
        report.Target.Should().Be(client.Address.ToString());

        // 🚨 The ORIGIN half — the one #1140 needed and did not have. It must name the frame that
        // posted, which here is this test method.
        var origin = Origins().Should().ContainSingle(
            "the origin detector must report the same violation from the POSTING side").Subject;
        origin.Role.Should().Be("sender");
        origin.Sender.Should().Be(Mesh.Address.ToString());
        origin.CallSite.Should().Contain(nameof(TheCapture_RecordsAViolation_WhenTheRouterGenuinelyPostsWork),
            "a call site that does not name the caller is the property #1140 was missing — the "
            + "receiver-side line already carries the two addresses, and four re-filings over a "
            + "month could not turn them into a call site");
    }

    /// <summary>The receiver-side lines — <c>ROUTER_TRAFFIC:</c>, logged in <c>DeliverMessage</c>.</summary>
    private RouterTrafficRecord[] Reports() => _capture.Records.Where(r => !r.IsOrigin).ToArray();

    /// <summary>The origin-side lines — <c>ROUTER_TRAFFIC ORIGIN:</c>, logged in <c>Post</c>.</summary>
    private RouterTrafficRecord[] Origins() => _capture.Records.Where(r => r.IsOrigin).ToArray();

    private void DumpReports()
    {
        foreach (var record in _capture.Records)
            Output.WriteLine($"ROUTER_TRAFFIC captured: {record}");
    }

    private sealed record RouterTrafficRecord(
        bool IsOrigin, string MessageType, string Role, string Sender, string Target, string CallSite);

    /// <summary>
    /// Reads the detector's own ERROR out of the logging pipeline — structured state, not the
    /// formatted string, so the assertions pin the VALUES the detector chose rather than the prose
    /// around them. Same shape as <c>RouterTrafficDetectorTest</c>'s capture; duplicated rather than
    /// shared because that one lives in a different test assembly on a different base.
    /// </summary>
    private sealed class RouterTrafficCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<RouterTrafficRecord> _records = new();

        internal RouterTrafficRecord[] Records => _records.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_records);

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
                var text = formatter(state, exception);
                var isOrigin = text.StartsWith("ROUTER_TRAFFIC ORIGIN:", StringComparison.Ordinal);
                if (!isOrigin && !text.StartsWith("ROUTER_TRAFFIC:", StringComparison.Ordinal))
                    return;

                sink.Enqueue(new RouterTrafficRecord(
                    isOrigin,
                    Value(values, "MessageType"),
                    Value(values, "Role"),
                    Value(values, "Sender"),
                    Value(values, "Target"),
                    Value(values, "CallSite")));
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
