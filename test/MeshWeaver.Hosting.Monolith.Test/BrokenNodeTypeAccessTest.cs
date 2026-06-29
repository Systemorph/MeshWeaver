using System;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Stand-in for a request type the broken NodeType's assembly would have
/// registered and handled. The test registers it on the CLIENT only — at the
/// broken instance's hub it arrives as RawJson (type not in that hub's
/// registry), exactly like real traffic to a non-compiling type.
/// </summary>
public record BrokenTypeProbeRequest : IRequest<BrokenTypeProbeResponse>;

public record BrokenTypeProbeResponse;

/// <summary>
/// 🚨 Contract: accessing a node whose NodeType CANNOT produce a usable hub
/// configuration (non-compiling source) must FAIL FAST with a terminal error —
/// never silence, never a phantom hang.
///
/// <para>The wedge this pins (atioz, 2026-06-12/13, user-reproduced): a
/// non-compiling NodeType gets the compilation-error overlay hub (default node
/// config + error Overview). That hub handles framework messages fine — but any
/// request type the BROKEN ASSEMBLY would have registered arrives as RawJson,
/// fails the <c>IRequest&lt;&gt;</c> check in <c>FinishDelivery</c>, and was
/// silently <c>Ignored()</c>: the caller parked forever. On Orleans the sibling
/// defect was worse — null-config enrichment results were silently filtered in
/// the grain's activation chain, parking every <c>DeliverMessage</c>.</para>
///
/// <para>The fix contract (<see cref="UnhandledMessageNack"/>): the overlay /
/// fallback hub answers everything it cannot handle with a
/// <see cref="DeliveryFailure"/> of <see cref="ErrorType.CompilationFailed"/>
/// naming the broken NodeType — surfaced to <c>hub.Observe</c> callers as a
/// terminal OnError. What the hub CAN handle (Ping, the error Overview layout)
/// keeps working so the GUI renders the diagnostic instead of dying.</para>
/// </summary>
public class BrokenNodeTypeAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "TestBrokenType";
    private const string NodeTypeId = "BrokenSample";
    private const string NodeTypePath = $"{Partition}/{NodeTypeId}";

    [Fact(Timeout = 120_000)]
    public async Task AccessingInstance_OfNonCompilingNodeType_AnswersTerminalError_NotSilence()
    {
        var workspace = Mesh.GetWorkspace();

        // 1. NodeType whose Configuration string is not valid C# — the kickoff
        //    compile fails and CompilationStatus settles at Error.
        await NodeFactory.CreateNode(new MeshNode(NodeTypeId, Partition)
        {
            Name = "Broken Sample Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Deliberately non-compiling NodeType.",
                Configuration = "config => this is not valid C# at all ((await ("
            }
        }).Should().Emit();

        // Wait for the compile to settle at Error (cold Roslyn compile budget).
        await workspace.GetMeshNodeStream(NodeTypePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error);
        Output.WriteLine("NodeType compile settled at Error.");

        // 2. An instance of the broken type.
        var instancePath = $"{Partition}/broken-instance";
        await NodeFactory.CreateNode(new MeshNode("broken-instance", Partition)
        {
            Name = "Broken Instance",
            NodeType = NodeTypePath,
            State = MeshNodeState.Active
        }).Should().Emit();
        Output.WriteLine("Instance created.");

        // Probe types registered on the CLIENT only — the broken instance's hub
        // has no registration for them (the broken assembly would have supplied
        // it), so the request arrives there as RawJson.
        var client = GetClient(c => ConfigureClient(c)
            .WithType(typeof(BrokenTypeProbeRequest), nameof(BrokenTypeProbeRequest))
            .WithType(typeof(BrokenTypeProbeResponse), nameof(BrokenTypeProbeResponse)));

        // 3a. LIVENESS: the overlay hub still answers what it CAN handle — Ping
        //     must succeed so the GUI can render the error Overview. Guards
        //     against over-NACKing handled framework messages.
        await client.Observe<PingResponse>(new PingRequest(), o => o.WithTarget(new Address(instancePath)))
            .Should().Within(60.Seconds()).Emit();
        Output.WriteLine("Ping answered — overlay hub is alive.");

        // 3b. THE CONTRACT: a request the broken type would have handled must be
        //     answered with a TERMINAL ERROR within budget — never silence (the
        //     pre-fix behavior: RawJson → not IRequest → Ignored → caller parks),
        //     never a phantom success.
        var notification = await client
            .Observe<BrokenTypeProbeResponse>(new BrokenTypeProbeRequest(),
                o => o.WithTarget(new Address(instancePath)))
            .Materialize()
            .Where(n => n.Kind is NotificationKind.OnError or NotificationKind.OnNext)
            .Should().Within(60.Seconds()).Emit();

        Output.WriteLine($"Notification: {notification.Kind} " +
            (notification.Kind == NotificationKind.OnError
                ? notification.Exception!.Message
                : notification.Value?.ToString()));

        notification.Kind.Should().Be(NotificationKind.OnError,
            "a non-compiling NodeType must answer unhandled requests with a DeliveryFailure (surfaced as OnError), never silence and never success");

        // The NACK must be actionable: typed CompilationFailed + the NodeType
        // path, so callers (and the GUI's NamedAreaView) know WHAT is broken.
        var failureException = notification.Exception.Should().BeOfType<DeliveryFailureException>(
            "the terminal error must be the typed DeliveryFailure NACK").Subject;
        failureException.Failure.ErrorType.Should().Be(ErrorType.CompilationFailed);
        failureException.Failure.NodeTypePath.Should().Be(NodeTypePath,
            "the NACK must name the broken NodeType so the caller can act on it");
    }
}
