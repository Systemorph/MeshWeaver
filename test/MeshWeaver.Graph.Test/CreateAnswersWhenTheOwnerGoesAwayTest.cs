using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#3510, the remaining half — the CREATE leg had no owner-side disposal NACK, so a hub
/// disposed while it was handling a <see cref="CreateNodeRequest"/> answered NOTHING.</b>
///
/// <para><b>The asymmetry this closes, stated by the issue itself.</b> On the UPDATE leg the owner
/// registers <c>DataExtensions.RegisterOwnerDisposingNack</c>, so a patch in flight across the
/// owner's teardown is answered <see cref="MeshNodeErrorCode.OwnerDisposing"/> and the writer
/// re-enqueues against the fresh activation — measured green by
/// <c>UpsertAnswersWhenTheOwnerGoesAwayTest</c> next to this file. On the CREATE leg there was no
/// such registration at all: <c>MeshExtensions.HandleCreateNodeRequest</c> returns
/// <c>Processed()</c> immediately and owes its reply from a DETACHED reactive chain, whose only
/// backstop is Rx's <c>onCompleted</c> arm. A chain that never TERMINATES — because a leg of it is
/// waiting on something that died with the hub — is exactly what neither <c>DefaultIfEmpty</c> nor
/// <c>Catch</c> can observe, so the requester was left with silence and burned its whole budget.</para>
///
/// <para><b>The seam this test parks is the one the inner create actually traverses.</b> The issue
/// records two failed attempts to reproduce it by parking a partition root's primary stream — "the
/// root's <c>Version</c> never advances after the post, so the create does not route through the
/// parked hub at all". So the park here is an <see cref="INodeValidator"/>: the creation validators
/// are run by <c>HandleCreateNodeRequest</c> itself, sequentially, between the partition bootstrap
/// and the save. A validator that never emits parks the create chain INSIDE the handling hub, which
/// is precisely the state CD 7950's trail ends in
/// (<c>HANDLER_ENTER → UPSERT_READ absent → create → HANDLER_EXIT state=Processed ⇒ (nothing)</c>).</para>
///
/// <para>🚨 <b>The OWNER hub is disposed, never the mesh.</b> Disposing the mesh would take the
/// caller's own subscription down with it, so "the caller was answered" could not be observed
/// whatever the framework did — the lesson <c>UpsertAnswersWhenTheOwnerGoesAwayTest</c> and
/// <c>NackReachesTheWaiterDuringTeardownTest</c> both record. Here the parent stays
/// <c>Started</c>, which is also the route the NACK travels: a hub past
/// <c>DisposeHostedHubs</c> cannot post for itself.</para>
///
/// <para>🚨 <b>No hand-woven gate anywhere.</b> The park is a cold <see cref="IObservable{T}"/> that
/// never emits — Rx's own idle state, not a lock — and the validator's "I was entered" signal is a
/// <see cref="ReplaySubject{T}"/> the test awaits through the assertion helpers. Nothing sleeps,
/// nothing polls a shared flag, and no <see cref="TimeSpan"/> literal appears.</para>
/// </summary>
public class CreateAnswersWhenTheOwnerGoesAwayTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Path infix that opts a create INTO the park. Everything else validates normally, which is
    /// what makes the control case below a real control rather than a differently-worded copy.
    /// </summary>
    private const string ParkMarker = "create-nack-parked";

    /// <summary>
    /// 🚨 A creation validator that PARKS — it signals that it was entered and then never emits,
    /// never completes and never faults. That is Rx's fourth outcome, the one a
    /// <c>Subscribe(onNext, onError)</c> plus an <c>onCompleted</c> arm still settles as silence,
    /// and it is what a leg waiting on a hub that has gone away looks like from inside the create
    /// chain. Registered as a real <see cref="INodeValidator"/> in the mesh's own service
    /// collection: the house rule is no mocking of framework interfaces, and this is not a mock of
    /// one — it is a genuine validator whose answer never arrives.
    /// </summary>
    private sealed class ParkingCreateValidator : INodeValidator
    {
        private readonly ReplaySubject<string> entered = new(1);

        /// <summary>Emits the path of the first create this validator parked.</summary>
        public IObservable<string> Entered => entered;

        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } =
            new[] { NodeOperation.Create };

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        {
            if (context.Node.Path?.Contains(ParkMarker, StringComparison.Ordinal) != true)
                return Observable.Return(NodeValidationResult.Valid());

            return Observable.Create<NodeValidationResult>(_ =>
            {
                entered.OnNext(context.Node.Path);
                // No emission, no completion, no fault — the park.
                return Disposable.Empty;
            });
        }
    }

    // Field initializers run BEFORE the base constructor body, which is where ConfigureMesh is
    // called — so the instance registered below is the one this test later fences on.
    private readonly ParkingCreateValidator parkingValidator = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services => services.AddSingleton<INodeValidator>(parkingValidator));

    /// <summary>
    /// 🚨 The regression. A create parked inside a per-node hub, and that hub disposed under it,
    /// must be ANSWERED — with <see cref="MeshNodeErrorCode.OwnerDisposing"/>, the code whose whole
    /// contract is "this activation went away; retrying against the fresh one is meaningful". The
    /// assertion is on the VERDICT, not merely on "something came back": a framework
    /// <c>DeliveryFailure</c> or a caller-side timeout would also end the wait, and neither is what
    /// this issue asks for — an install that reads <c>OwnerUnreachable</c> or "the outcome is
    /// unknown" still fails, where one that reads <c>OwnerDisposing</c> re-drives and succeeds.
    /// </summary>
    [Fact]
    public async Task OwnerDisposedUnderTheCreate_AnswersOwnerDisposing_InsteadOfGoingSilent()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ownerId = $"create-nack-owner-{suffix}";
        var ownerPath = $"{TestPartition}/{ownerId}";
        await NodeFactory.CreateNode(
                new MeshNode(ownerId, TestPartition) { Name = "owner", NodeType = "Markdown" })
            .Should().Emit();

        // Force the owner's activation: the hub must EXIST before it can be disposed under a
        // create, and it must be the hub that HANDLES the create (per-node hubs register the node
        // operation handlers through AddMeshDataSource).
        await RequestHub.Observe(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(ownerPath)))
            .Should().Emit();
        var owner = Mesh.GetHostedHub(new Address(ownerPath), HostedHubCreation.Never);
        owner.Should().NotBeNull(
            "the create must be handled by a hub that exists, or the scenario is not the one #3510 names");

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        // The producer → test signal, awaited through the assertion helpers: a ReplaySubject the
        // subscription completes. Never a hand-woven gate, and never a poll over a shared field.
        var answered = new ReplaySubject<CreateNodeResponse>(1);
        var targetId = $"{ParkMarker}-{suffix}";
        using var create = access
            .RunAsSystem(() => ObserveNodeOperation(
                new CreateNodeRequest(
                    new MeshNode(targetId, TestPartition) { Name = "parked", NodeType = "Markdown" }),
                o => o.WithTarget(new Address(ownerPath))))
            .Subscribe(
                d =>
                {
                    answered.OnNext(d.Message);
                    answered.OnCompleted();
                },
                answered.OnError);
        Output.WriteLine($"[create] issued for {TestPartition}/{targetId} at owner {ownerPath}");

        // Fence on the create actually being PARKED inside the owner. Without it the test could
        // pass by asserting about a create that had not yet reached the handler at all.
        await parkingValidator.Entered.Should().Within(TestTimeouts.Convergence).Emit(
            "the create must be parked inside the owner's own handler before the owner is disposed");
        Output.WriteLine("[fence] the create chain is parked inside the owner's creation validators");

        owner!.Post(
            new DisposeRequest
            {
                Reason = "CreateAnswersWhenTheOwnerGoesAwayTest: disposing the owner while it "
                         + "handles a create, to drive #3510's create leg",
            },
            o => o.WithTarget(owner.Address));
        Output.WriteLine("[dispose] DisposeRequest posted to the owner while the create is in flight");

        // The bound is deliberately far below the caller's own RequestTimeout: an answer that only
        // arrived at a bound would be indistinguishable from the defect. A FAULT here fails too, and
        // says so — the owner's disposal must produce a verdict the requester can act on, not an
        // exception it has to classify.
        var answer = await answered.Should().Within(TestTimeouts.Convergence).Emit(
            "a create whose handling hub is disposed under it must be ANSWERED — silence here is "
            + "#3510: the installer then runs out its whole bound with no name for what happened");

        Output.WriteLine(
            $"[caller] success={answer.Success} reason={answer.RejectionReason} "
            + $"code={answer.NodeError?.Code} error={answer.Error}");

        answer.NodeError.Should().NotBeNull(
            "the answer must be STRUCTURED, so the caller can switch on the code rather than "
            + "pattern-match a sentence");
        answer.NodeError!.Code.Should().Be(MeshNodeErrorCode.OwnerDisposing,
            "OwnerDisposing is the one code whose contract is 'this activation went away; a retry "
            + "against the fresh one is meaningful' — the UPDATE leg has minted it since #3499, and "
            + "the asymmetry with the CREATE leg is what keeps #3510 open");
        answer.NodeError.Path.Should().Be(ownerPath,
            "the verdict must name the hub that went away, not the node being created — that is "
            + "what makes the next occurrence attributable from the requester's side alone");
    }

    /// <summary>
    /// 🚨 The control, in the other direction. A fix that answered <c>OwnerDisposing</c> for every
    /// create — or a park that swallowed every create — would satisfy the regression above while
    /// silently breaking creation itself. This one takes the same hub, the same identity and the
    /// same request shape, differing ONLY in the path that opts into the park.
    /// </summary>
    [Fact]
    public async Task ACreateOnAHubThatStaysUp_StillSucceeds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ownerId = $"create-live-owner-{suffix}";
        var ownerPath = $"{TestPartition}/{ownerId}";
        await NodeFactory.CreateNode(
                new MeshNode(ownerId, TestPartition) { Name = "owner", NodeType = "Markdown" })
            .Should().Emit();
        await RequestHub.Observe(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(ownerPath)))
            .Should().Emit();

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var targetId = $"create-live-target-{suffix}";
        var answer = await access
            .RunAsSystem(() => ObserveNodeOperation(
                new CreateNodeRequest(
                    new MeshNode(targetId, TestPartition) { Name = "live", NodeType = "Markdown" }),
                o => o.WithTarget(new Address(ownerPath))))
            .Should().Within(TestTimeouts.Convergence).Emit();

        answer.Message.Success.Should().BeTrue(
            $"the same request shape on a hub that stays up must still create: {answer.Message.Error}");
        answer.Message.NodeError.Should().BeNull(
            "a successful create carries no structured error — otherwise the regression above "
            + "would pass on a fix that stamped OwnerDisposing unconditionally");
    }
}
