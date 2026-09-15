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
    /// Path infix that parks the FIRST create for a path and passes every later one — the lever the
    /// convergence case needs, because a re-drive that met the same park would never finish and the
    /// test could only ever assert "answered", never "recovered".
    /// </summary>
    private const string ParkOnceMarker = "create-nack-parkonce";

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

        // Instance state on a mesh-scoped singleton, never static: the repository's no-static-state
        // rule holds in test/ too, and a static here would bleed across test classes.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> parkedOnce =
            new(StringComparer.Ordinal);

        /// <summary>Emits the path of each create this validator parked.</summary>
        public IObservable<string> Entered => entered;

        /// <summary>How many creates have been parked — the denominator the convergence case
        /// reads, so "it recovered" is distinguishable from "it never parked in the first
        /// place".</summary>
        public int ParkedCount => parkedOnce.Count;

        public IReadOnlyCollection<NodeOperation> SupportedOperations { get; } =
            new[] { NodeOperation.Create };

        public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
        {
            var path = context.Node.Path;
            if (path is null)
                return Observable.Return(NodeValidationResult.Valid());

            if (path.Contains(ParkOnceMarker, StringComparison.Ordinal))
                // TryAdd is the once-only claim: the FIRST create for this path parks, every
                // re-drive of it validates normally.
                return parkedOnce.TryAdd(path, 0) ? Park(path) : Observable.Return(NodeValidationResult.Valid());

            return path.Contains(ParkMarker, StringComparison.Ordinal)
                ? Park(path)
                : Observable.Return(NodeValidationResult.Valid());
        }

        private IObservable<NodeValidationResult> Park(string path) =>
            Observable.Create<NodeValidationResult>(_ =>
            {
                entered.OnNext(path);
                // No emission, no completion, no fault — the park.
                return Disposable.Empty;
            });
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

    /// <summary>
    /// 🚨 <b>The SANCTIONED caller — <c>IMeshService.CreateNode</c> — is answered in milliseconds
    /// with a NAME, instead of waiting out its budget.</b> That is #3510's Expectation verbatim:
    /// *"the recycle answers every in-flight write under it with a NACK the nodeops handler turns
    /// into the installer's reply, so the install fails in milliseconds with a name instead of
    /// running out a 10-minute bound."*
    ///
    /// <para><b>Why this case exists beside the one above, rather than duplicating it.</b> The
    /// first case posts a <see cref="CreateNodeRequest"/> directly and reads the response; this one
    /// goes through the API every production writer actually uses, whose failure surface is an
    /// <see cref="Exception"/> on the observable, not a response object. The NACK has to survive
    /// that translation — through the owner's parent, into the caller's callback, through
    /// <c>ToException</c> — and "the verdict was minted" says nothing about whether it arrived
    /// there.</para>
    ///
    /// <para>🚨 <b>What it deliberately does NOT assert: a recovery.</b> A re-drive was written and
    /// measured against this exact rig and does not work on this lane —
    /// <c>portal/nodeops-{meshId}</c> is a stream-routed address that is unregistered with its hub
    /// and is not re-materialised by posting to it, so the re-post is answered
    /// <c>RouteMessage: NotFound … No node found at 'portal/nodeops-…'</c> and buys one extra round
    /// trip before the identical failure. Asserting a convergence this transport cannot deliver
    /// would be a test written for a promise rather than for the code.</para>
    ///
    /// <para>The bound is what carries the claim: the answer must arrive well inside
    /// <see cref="TestTimeouts.Quick"/>, far below <c>InnerCreateVerdictBound</c> (36 s) — an answer
    /// that only arrived at a bound is indistinguishable from the defect.</para>
    /// </summary>
    [Fact]
    public async Task TheSanctionedCallerIsAnsweredAtOnce_NotLeftToItsBudget()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var targetId = $"{ParkMarker}-svc-{suffix}";

        // The node-operation execution hub the sanctioned caller targets. Resolved through the
        // framework's own walk, never a hand-built address, so a change to where node CRUD executes
        // moves this test with it rather than leaving it asserting about a hub nobody uses.
        var executorAddress = RequestHub.NodeOperationTarget();
        Output.WriteLine($"[target] node operations execute on {executorAddress}");

        // 🚨 Resolved from the CALLER's hub, not the mesh's. `IMeshService` is AddScoped over
        // `IMessageHub`, and `NodeOperationIssuingHub` returns the resolving hub UNCHANGED for
        // anything that is not the router — so a per-node, portal, session or import-hub caller
        // issues from its OWN block while the work executes on `portal/nodeops-{meshId}`. Resolving
        // from `Mesh` instead collapses issuer and executor onto one hub, and disposing it then
        // kills the caller's own callback: measured here as `HubDisposedBeforeResponseException`,
        // which is a different defect and not this one.
        var callerService = RequestHub.ServiceProvider.GetRequiredService<IMeshService>();
        var outcome = new ReplaySubject<Exception>(1);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using var create = access
            .RunAsSystem(() => callerService.CreateNode(
                new MeshNode(targetId, TestPartition) { Name = "parked", NodeType = "Markdown" }))
            .Subscribe(
                node => outcome.OnError(new InvalidOperationException(
                    $"the parked create must not succeed; it emitted {node.Path}")),
                ex =>
                {
                    outcome.OnNext(ex);
                    outcome.OnCompleted();
                });

        await parkingValidator.Entered.Should().Within(TestTimeouts.Convergence).Emit(
            "the create must be parked inside the executing hub before that hub is disposed");
        var executor = Mesh.GetHostedHub(executorAddress, HostedHubCreation.Never);
        executor.Should().NotBeNull("the parked create proves the executing hub exists");
        Output.WriteLine("[fence] the create is parked inside the node-operation execution hub");

        executor!.Post(
            new DisposeRequest
            {
                Reason = "CreateAnswersWhenTheOwnerGoesAwayTest: disposing the node-operation "
                         + "execution hub while it handles a create, to drive #3510's create leg "
                         + "through the sanctioned caller",
            },
            o => o.WithTarget(executor.Address));
        Output.WriteLine("[dispose] DisposeRequest posted to the executing hub");

        var error = await outcome.Should().Within(TestTimeouts.Quick).Emit(
            "the sanctioned caller must be told, AT ONCE, that the activation went away — silence "
            + "until InnerCreateVerdictBound is #3510: the installer then runs out its whole bound "
            + "with no name for what happened");

        Output.WriteLine($"[caller] {error.GetType().Name}: {error.Message}");

        // 🚨 The STRUCTURED check first: a caller that only ever sees the exception must be able to
        // act on the code rather than parse a sentence. That is what NodeErrorKey is for.
        var nodeError = error.Data[NodeCreationFailure.NodeErrorKey] as MeshNodeError;
        nodeError.Should().NotBeNull(
            "the structured verdict must survive the translation to an exception — the sanctioned "
            + "caller's failure surface IS an exception, so dropping it here would leave every "
            + "programmatic consumer pattern-matching a sentence");
        nodeError!.Code.Should().Be(MeshNodeErrorCode.OwnerDisposing,
            "'the activation went away' is not a verdict about the request, and only the code can "
            + "say so");
        nodeError.Path.Should().Be(executorAddress.ToString(),
            "it must name the activation that went away, so the next occurrence is attributable "
            + "from the requester's side alone");

        // And the sentence too, because the requester is usually in another process and a log
        // reader there has nothing else to go on.
        error.Message.Should().Contain("disposed",
            "the failure must NAME the teardown, not merely fail — 'the outcome is unknown' is the "
            + "answer this change exists to replace");
        error.Message.Should().Contain(executorAddress.ToString(),
            "and the hub must be named in the words as well as in the payload");
    }
}
