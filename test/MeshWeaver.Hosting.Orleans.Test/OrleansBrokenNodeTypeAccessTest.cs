using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Stand-in for a request type the broken NodeType's assembly would have
/// registered — registered on the CLIENT only, so at the broken instance's
/// grain hub it arrives as RawJson. Mirrors
/// <c>MeshWeaver.Hosting.Monolith.Test.BrokenNodeTypeAccessTest</c>.
/// </summary>
public record OrleansBrokenTypeProbeRequest : IRequest<OrleansBrokenTypeProbeResponse>;

public record OrleansBrokenTypeProbeResponse;

/// <summary>
/// 🚨 Orleans mirror of <c>BrokenNodeTypeAccessTest</c>: accessing an instance
/// of a NON-COMPILING NodeType over the real grain path must answer a TERMINAL
/// error — never park the caller (the atioz wedge of 2026-06-12: every
/// <c>DeliverMessage</c> burned the 30 s Orleans call timeout, the GUI
/// resubscribed, and the storm wedged the space).
///
/// <para>Covers the full grain pipeline: RoutingGrain → MessageHubGrain
/// activation → NodeType enrichment settling at <c>CompilationStatus.Error</c>
/// → compilation-error-overlay hub with the <see cref="UnhandledMessageNack"/>
/// policy → typed <see cref="DeliveryFailure"/>
/// (<see cref="ErrorType.CompilationFailed"/> + NodeTypePath) NACKed back
/// through the Orleans stream to the client's <c>Observe</c> callback.</para>
/// </summary>
public class OrleansBrokenNodeTypeAccessTest(ITestOutputHelper output)
    : OrleansTestBase<DynamicCompilationSiloConfigurator>(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithType(typeof(OrleansBrokenTypeProbeRequest), nameof(OrleansBrokenTypeProbeRequest))
            .WithType(typeof(OrleansBrokenTypeProbeResponse), nameof(OrleansBrokenTypeProbeResponse));

    private IMessageHub SiloMesh =>
        ((InProcessSiloHandle)Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>();

    [Fact(Timeout = 120_000)]
    public async Task Instance_OfNonCompilingNodeType_AnswersTerminalError_NotSilence()
    {
        var ct = new CancellationTokenSource(110.Seconds()).Token;
        var client = GetClient($"broken-{Guid.NewGuid():N}");

        var typeId = $"OrleansBrokenAccess{Guid.NewGuid():N}";
        var typePath = $"type/{typeId}";

        // 1. NodeType whose Configuration string is not valid C# — the kickoff
        //    compile fails and CompilationStatus settles at Error. Seed via the
        //    silo's IMeshService (see OrleansDynamicCompilationTest class remarks
        //    on why not through the client mesh).
        var meshService = SiloMesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(MeshNode.FromPath(typePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Deliberately non-compiling NodeType (Orleans).",
                Configuration = "config => this is not valid C# at all ((("
            }
        }).FirstAsync().ToTask(ct);

        // First touch of the NodeType stream kicks the compile; wait for the
        // terminal Error state (cold Roslyn compile budget).
        await SiloMesh.GetWorkspace().GetMeshNodeStream(typePath)
            .Where(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error)
            .FirstAsync()
            .Timeout(90.Seconds())
            .ToTask(ct);
        Output.WriteLine("NodeType compile settled at Error.");

        // 2. An instance of the broken type.
        var instancePath = $"{typePath}/broken-instance";
        await meshService.CreateNode(MeshNode.FromPath(instancePath) with
        {
            Name = "broken-instance",
            NodeType = typePath,
            State = MeshNodeState.Active
        }).FirstAsync().ToTask(ct);
        Output.WriteLine("Instance created.");

        // 3a. LIVENESS: the grain must ACTIVATE (overlay hub) and answer what it
        //     can — no eternal activation park. Pre-fix, an activation that never
        //     resolved parked this for the full budget.
        await client.Observe<PingResponse>(new PingRequest(),
                o => o.WithTarget(new Address(instancePath)))
            .FirstAsync()
            .Timeout(60.Seconds())
            .ToTask(ct);
        Output.WriteLine("Ping answered — overlay hub activated on the grain.");

        // 3b. THE CONTRACT: a request the broken type would have handled must be
        //     answered with a typed terminal error within budget — never silence,
        //     never phantom success.
        var notification = await client
            .Observe<OrleansBrokenTypeProbeResponse>(new OrleansBrokenTypeProbeRequest(),
                o => o.WithTarget(new Address(instancePath)))
            .Materialize()
            .Where(n => n.Kind is NotificationKind.OnError or NotificationKind.OnNext)
            .FirstAsync()
            .Timeout(60.Seconds())
            .ToTask(ct);

        Output.WriteLine($"Notification: {notification.Kind} " +
            (notification.Kind == NotificationKind.OnError
                ? notification.Exception!.Message
                : notification.Value?.ToString()));

        notification.Kind.Should().Be(NotificationKind.OnError,
            "a non-compiling NodeType must answer unhandled requests with a DeliveryFailure (surfaced as OnError), never silence and never success");

        var failureException = notification.Exception.Should().BeOfType<DeliveryFailureException>(
            "the terminal error must be the typed DeliveryFailure NACK").Subject;
        failureException.Failure.ErrorType.Should().Be(ErrorType.CompilationFailed);
        failureException.Failure.NodeTypePath.Should().Be(typePath,
            "the NACK must name the broken NodeType so the caller can act on it");
    }
}
