using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json.Serialization;
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
/// 🚨 <b>A create whose handling hub is disposed while the create is still in flight must not
/// throw out of its own completion.</b>
///
/// <para><b>The incident.</b> Core CI run 36104469492 (attempt 1, shard 4):
/// <c>MeshWeaver.Compiler.Pipeline.Test</c> reported 1102 passed and then exited <c>2</c> —
/// xUnit's <c>AppDomain.UnhandledException</c> handler. The job log carries the fault as
/// <c>[FATAL ERROR] System.ObjectDisposedException</c>, raised on a ThreadPool thread from
/// <c>HandleCreateNodeRequest</c>'s post-creation completion arm: after the create answered its
/// caller, <c>ActivatePendingControlPlane</c> (#3153) called <c>hub.GetMeshNodeStream(path)</c>, which
/// resolves the workspace out of the handling hub's DI scope EAGERLY — and the caller, having
/// received its answer, had already disposed that hub. A synchronous throw inside a
/// <c>Subscribe</c> callback reaches no error arm: it unwinds into whatever thread completed the
/// chain and kills the process.</para>
///
/// <para><b>Made deterministic.</b> A real <see cref="INodePostCreationHandler"/> parks the create
/// AFTER the row is written (so it is genuinely in flight) and records the hub HANDLING it (the one
/// its own DI scope resolves). The test disposes that hub, proves its scope is closed, and only then
/// lets the handler complete — on the test thread, so the completion arm runs synchronously inside
/// <c>LetThrough</c> and a throw from it surfaces as an exception here instead of as a dead host.</para>
///
/// <para>🚨 No hand-woven gate: the park is an <see cref="AsyncSubject{T}"/> completed in a
/// <c>finally</c>; "entered" is a <see cref="ReplaySubject{T}"/> awaited through the assertion
/// helpers. Nothing sleeps.</para>
/// </summary>
public class ACreateCompletingOnADisposedHubTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ParkedNodeType = "Test/ParkedControlPlane";

    /// <summary>Content carrying a pending <c>RequestedXxx</c> — what makes the create activate its owner.</summary>
    public record ParkedControlPlaneContent
    {
        [JsonPropertyName("requestedAction")]
        public string? RequestedAction { get; init; }
    }

    /// <summary>Instance state shared between the test and every handler instance the hubs resolve.</summary>
    private sealed class Park
    {
        private readonly ReplaySubject<IMessageHub> entered = new(1);
        private readonly AsyncSubject<Unit> letThrough = new();

        /// <summary>Emits the hub handling each parked create.</summary>
        public IObservable<IMessageHub> Entered => entered;

        /// <summary>Completes every parked handler. Idempotent.</summary>
        public void LetThrough()
        {
            letThrough.OnNext(Unit.Default);
            letThrough.OnCompleted();
        }

        public IObservable<Unit> Hold(IMessageHub handlingHub) => Observable.Defer(() =>
        {
            entered.OnNext(handlingHub);
            return letThrough;
        });
    }

    /// <summary>
    /// A post-creation handler resolved from the HANDLING hub's scope, so the
    /// <see cref="IMessageHub"/> it is built with is the hub running <c>HandleCreateNodeRequest</c>.
    /// </summary>
    private sealed class ParkingPostCreationHandler(Park park, IMessageHub handlingHub) : INodePostCreationHandler
    {
        public string NodeType => ParkedNodeType;

        public IObservable<Unit> Handle(MeshNode createdNode, string? createdBy) => park.Hold(handlingHub);
    }

    // Field initializers run before the base constructor calls ConfigureMesh. Instance, never static.
    private readonly Park park = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(ParkedNodeType) { Name = "Parked control plane" })
            .ConfigureHub(config => config.WithType<ParkedControlPlaneContent>(nameof(ParkedControlPlaneContent)))
            .ConfigureServices(services => services.AddTransient<INodePostCreationHandler>(
                sp => new ParkingPostCreationHandler(park, sp.GetRequiredService<IMessageHub>())));

    [Fact(Timeout = 120_000)]
    public async Task ItsCompletion_AfterTheHandlingHubIsDisposed_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var node = new MeshNode("parked-create", TestPartition)
        {
            NodeType = ParkedNodeType,
            Name = "Parked create",
            State = MeshNodeState.Active,
            // A pending control-plane request: the create's completion will try to activate the owner.
            Content = new ParkedControlPlaneContent { RequestedAction = "Activate" },
        };

        var terminal = new ReplaySubject<Notification<MeshNode>>(1);
        using var creating = meshService.CreateNode(node).Materialize().Subscribe(terminal.OnNext);
        try
        {
            var handlingHub = await park.Entered
                .Should().Within(TestTimeouts.Convergence)
                .Emit("the create must be written and then held in its post-creation handler",
                    cancellationToken: ct);
            handlingHub.Should().NotBeSameAs(Mesh,
                "the create is handled off the router — disposing the router would end the test mesh");

            var down = handlingHub.DisposalCompleted.Take(1);
            handlingHub.Dispose();
            await down.Should().Within(TestTimeouts.Convergence)
                .Emit("the handling hub must actually finish disposing", cancellationToken: ct);
            Action resolveFromTheDeadHub = () => handlingHub.ServiceProvider.GetService<ILoggerFactory>();
            resolveFromTheDeadHub.Should().Throw<ObjectDisposedException>(
                "CONTROL: the handling hub's scope must be closed, or this case measures nothing");
        }
        finally
        {
            // The completion arm runs synchronously on THIS thread: before the fix, the
            // ObjectDisposedException from the control-plane activation came out of this call —
            // on a pool thread in CI, where it killed the test host with exit 2.
            Action release = park.LetThrough;
            release.Should().NotThrow(
                "a create completing after its handling hub is gone must report what it could not do, "
                + "never throw out of its own Subscribe callback");
        }

        await terminal.Take(1).Should().Within(TestTimeouts.Convergence)
            .Emit("the caller is always answered — by the create or by the owner-disposing NACK",
                cancellationToken: ct);
    }
}
