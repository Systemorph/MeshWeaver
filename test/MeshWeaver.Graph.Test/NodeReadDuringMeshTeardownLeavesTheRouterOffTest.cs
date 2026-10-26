using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Subjects;
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
/// 🚨 <b>Regression pin for
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/7136">#7136</see></b> — production's
/// <c>ROUTER_TRAFFIC ORIGIN: GetDataRequest was POSTED with the mesh hub as sender</c>, call site
/// <c>MeshNodeStreamExtensions.GetMeshNodeOutcome</c>'s <c>Issue()</c>.
///
/// <para><b>The defect.</b> <c>GetMeshNodeOutcome</c> hops a router caller onto
/// <c>MeshExtensions.ReadIssuingHub()</c>, which is <c>MeshReadHub() ?? hub</c>. <c>MeshReadHub()</c>
/// returns <c>null</c> once the mesh is past <see cref="MessageHubRunLevel.DisposeHostedHubs"/> — it
/// refuses to materialise a hub during disposal — so in exactly that window the fallback handed back
/// the ROUTER and the read was posted from <c>mesh/{id}</c>. Every mesh-singleton that holds the
/// DI-injected root hub and reacts to the hosted-hub teardown with a node read takes that path.</para>
///
/// <para><b>The scenario is the production one, driven deterministically.</b> A hub HOSTED by the
/// mesh registers a disposal action; the mesh disposes its hosted hubs in its
/// <see cref="MessageHubRunLevel.DisposeHostedHubs"/> phase, so that action runs with the mesh in
/// precisely the window the fallback covered — and it issues the read the way a mesh-singleton does,
/// on the root mesh hub. Both are asserted: that the mesh WAS in that window when the read was issued
/// (otherwise the test proves nothing about it), and that the origin detector stayed silent.</para>
///
/// <para><b>The negative control</b> is the same test against the pre-fix seam: it reports the origin
/// line naming <c>GetMeshNodeOutcome</c> with role <c>sender</c>, which is what makes a silence here
/// evidence rather than an unwired logger.</para>
/// </summary>
public class NodeReadDuringMeshTeardownLeavesTheRouterOffTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private readonly OriginCapture origins = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(origins));

    [Fact(Timeout = 120_000)]
    public async Task ANodeReadIssuedFromTheRouterWhileHostedHubsDispose_NeverLeavesTheRouter()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/teardown-read-target";
        var mesh = Mesh;

        // The production shape: an actor hosted by the mesh, whose teardown makes a mesh-singleton
        // read a node through the DI-injected ROOT hub.
        var reader = mesh.GetHostedHub(
            AddressExtensions.CreatePortalAddress($"teardown-reader-{Guid.NewGuid():N}"),
            c => c,
            HostedHubCreation.Always);
        reader.Should().NotBeNull("the hosted reader is the vehicle that runs inside the teardown window");

        var issued = new AsyncSubject<MessageHubRunLevel>();
        var outcome = new AsyncSubject<NodeReadOutcome>();
        reader!.RegisterForDisposal(_ =>
        {
            issued.OnNext(mesh.RunLevel);
            issued.OnCompleted();
            mesh.GetMeshNodeOutcome(path, TimeSpan.FromSeconds(5), ReadTimeoutBehavior.EmitNull)
                .Subscribe(
                    o => { outcome.OnNext(o); outcome.OnCompleted(); },
                    ex => outcome.OnError(ex));
        });

        mesh.Dispose();

        var runLevelAtIssue = await issued.Should().Within(30.Seconds()).Emit(
            "the hosted reader's disposal action runs when the mesh disposes its hosted hubs", ct);
        (runLevelAtIssue >= MessageHubRunLevel.DisposeHostedHubs).Should().BeTrue(
            $"the mesh was at {runLevelAtIssue}; the read must be issued in the teardown window #7136 is about — past DisposeHostedHubs, "
            + "where MeshReadHub() refuses to materialise the read hub — or this test measures nothing");

        var read = await outcome.Should().Within(30.Seconds()).Emit(
            "the read must settle either way — it must never be left pending by the teardown", ct);
        Output.WriteLine($"[read] status={read.Status} failure={read.Failure?.GetType().Name}: {read.Failure?.Message}");

        foreach (var record in origins.Records)
            Output.WriteLine($"[origin] {record}");
        origins.Records
            .Where(r => r.MessageType.Contains(nameof(GetDataRequest), StringComparison.Ordinal)
                        && r.Role.Contains("sender", StringComparison.Ordinal))
            .Should().BeEmpty(
                "#7136: a node read issued on the root mesh hub must never be POSTED from the router. "
                + "In the teardown window there is no off-router read hub left, so the read is not "
                + "issued at all and settles Unavailable — falling back to the router is the defect");

        read.Status.Should().Be(NodeReadStatus.Unavailable,
            "with no off-router hub to issue on, the read could not be performed — Unavailable is the "
            + "status that says exactly that, and says nothing about whether the node exists");
        read.Failure.Should().BeOfType<ObjectDisposedException>(
            "the failure must name the teardown that made the read unissuable");
    }

    /// <summary>
    /// The EARLIER half of the same teardown: <c>MessageHub.Dispose()</c> sets <c>IsDisposing</c> and
    /// closes hosted-hub creation (<c>HostedHubsCollection.CloseCreation</c>) before it enters
    /// Quiescing, so the run level still reads <see cref="MessageHubRunLevel.Started"/> while a read
    /// hub that does not exist yet can no longer be created. The mesh's own
    /// <see cref="IMessageHub.ShuttingDown"/> fires synchronously in exactly that window, which makes
    /// it the deterministic trigger. A read there must neither leave the router nor be misreported as
    /// a construction fault: it is teardown, and must say so.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ANodeReadIssuedFromTheRouterAfterCreationCloses_IsNamedAsTeardown()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/teardown-read-target";
        var mesh = Mesh;

        // PRECONDITION: the shared read hub must not exist yet. An existing one is still handed out
        // during the drain, and the read would then be issued from it, measuring nothing here.
        mesh.GetHostedHub(AddressExtensions.CreatePortalAddress($"reads-{mesh.Address.Id}"),
                HostedHubCreation.Never)
            .Should().BeNull("this case is about a read hub that teardown no longer lets be created");

        var issued = new AsyncSubject<(bool IsDisposing, MessageHubRunLevel RunLevel)>();
        var outcome = new AsyncSubject<NodeReadOutcome>();
        using var shuttingDown = mesh.ShuttingDown.Subscribe(_ =>
        {
            issued.OnNext((mesh.IsDisposing, mesh.RunLevel));
            issued.OnCompleted();
            mesh.GetMeshNodeOutcome(path, TimeSpan.FromSeconds(5), ReadTimeoutBehavior.EmitNull)
                .Subscribe(
                    o => { outcome.OnNext(o); outcome.OnCompleted(); },
                    ex => outcome.OnError(ex));
        });

        mesh.Dispose();

        var atIssue = await issued.Should().Within(30.Seconds()).Emit(
            "the mesh signals ShuttingDown synchronously at the start of its teardown", ct);
        atIssue.IsDisposing.Should().BeTrue("the read must be issued after Dispose() began");
        (atIssue.RunLevel < MessageHubRunLevel.DisposeHostedHubs).Should().BeTrue(
            $"the mesh was at {atIssue.RunLevel}; this case is the window BEFORE DisposeHostedHubs, "
            + "where only the creation freeze refuses the read hub");

        var read = await outcome.Should().Within(30.Seconds()).Emit(
            "the read must settle either way", ct);
        Output.WriteLine($"[read] status={read.Status} failure={read.Failure?.GetType().Name}: {read.Failure?.Message}");

        foreach (var record in origins.Records)
            Output.WriteLine($"[origin] {record}");
        origins.Records
            .Where(r => r.MessageType.Contains(nameof(GetDataRequest), StringComparison.Ordinal)
                        && r.Role.Contains("sender", StringComparison.Ordinal))
            .Should().BeEmpty("#7136: the read must never be POSTED from the router");

        read.Status.Should().Be(NodeReadStatus.Unavailable);
        read.Failure.Should().BeOfType<ObjectDisposedException>(
            "the read hub was refused by the teardown's creation freeze, not by a construction fault; "
            + "an InvalidOperationException here sends the reader looking for an error that never happened");
    }

    private sealed record OriginRecord(string MessageType, string Role, string Sender, string Target, string CallSite);

    /// <summary>Reads the detector's own <c>ROUTER_TRAFFIC ORIGIN</c> ERROR out of the logging
    /// pipeline as structured values (same shape as <c>RouterTrafficOnNodeCreateFromTheRootHubTest</c>).</summary>
    private sealed class OriginCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<OriginRecord> records = new();

        internal OriginRecord[] Records => records.ToArray();

        public ILogger CreateLogger(string categoryName) => new Capturing(records);

        public void Dispose() { }

        private sealed class Capturing(ConcurrentQueue<OriginRecord> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Error || state is not IReadOnlyList<KeyValuePair<string, object?>> values)
                    return;
                if (!formatter(state, exception).StartsWith("ROUTER_TRAFFIC ORIGIN:", StringComparison.Ordinal))
                    return;
                sink.Enqueue(new OriginRecord(
                    Value(values, "MessageType"), Value(values, "Role"), Value(values, "Sender"),
                    Value(values, "Target"), Value(values, "CallSite")));
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
