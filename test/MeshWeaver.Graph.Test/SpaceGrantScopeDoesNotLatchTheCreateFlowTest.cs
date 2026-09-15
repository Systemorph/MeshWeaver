using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>The Space creator-Admin grant does not LATCH the flow that created the Space as
/// <c>system-security</c></b> (#4061 — the mechanism behind the "intermittent" denial).
///
/// <para><c>SpacePostCreationHandler.Handle</c> writes the creator's <c>AccessAssignment</c> under
/// the System identity, which is correct and necessary: a brand-new partition root is a path its
/// own creator holds nothing on, so the grant that makes them its owner cannot be authorised as
/// them. What was wrong was the BOUNDARY. The site was written
/// <c>Observable.Using(() =&gt; accessService.ImpersonateAsSystem(), _ =&gt; meshService.CreateNode(grant))</c>,
/// and impersonation is an <c>AsyncLocal</c> store/restore pair: Rx runs the resource factory on the
/// SUBSCRIBING thread and disposes the resource when the inner observable TERMINATES — for this
/// cross-hub create, the owning hub's response thread. The restore is thread-affine
/// (<c>AccessService.AccessContextScope.Dispose</c>), so it writes nothing over there and
/// <b>nothing ever closes the scope on the subscriber</b>. The create flow that invoked the handler
/// keeps <c>system-security</c> for everything it does next.</para>
///
/// <para><b>Why that is #4061 and not a tidiness point.</b> The subscriber here is
/// <c>MeshExtensions.RunPostCreationHandlersObs</c>, which goes on to persist and ANNOUNCE the
/// <c>Admin/Partition/{id}</c> <see cref="PartitionDefinition"/>. #4197 measured both identities on
/// that announcement in ONE run, 41 ms apart — <c>user=system-security</c> when the latch was still
/// in effect and <c>user=Roland</c>, denied on <c>Admin/Partition/{id}</c>, when it was not — and
/// fixed the announcement by declaring its identity as a VALUE. That closed the symptom at one call
/// site. THIS closes the source: as long as the latch exists, every stage composed after the grant
/// runs as System by accident, and "works" only until the timing changes. An accidental
/// <c>Permission.All</c> is the more serious half — it is an escalation whose failure mode is a
/// write silently succeeding where the user would have been refused (#1444).</para>
///
/// <para>The cure is the framework's sealed boundary,
/// <c>ImpersonationScopeExtensions.RunAsSystem</c>: it enters the scope at Subscribe (so the cold
/// write still carries System, which <see cref="TheCreateStillWorksAndTheCreatorOwnsTheSpace"/>
/// pins) and leaves it on the way out of that same Subscribe. <c>SpaceNodeType.cs</c> was on
/// <c>test/ImpersonationScopeSites.allow</c> for exactly this shape; this change retires the line.</para>
///
/// <para>The fixture is the ORDINARY-USER shape — RLS on — because the grant only has to exist at
/// all when the creator is not already privileged on the partition.</para>
/// </summary>
public class SpaceGrantScopeDoesNotLatchTheCreateFlowTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SpaceId = "grantlatch";

    /// <summary>
    /// The identity the subscribing flow must still be running as after the handler is subscribed,
    /// and the Space's creator. It holds NOTHING anywhere else — see
    /// <see cref="TheCreateStillWorksAndTheCreatorOwnsTheSpace"/> for why that matters.
    /// </summary>
    private static readonly AccessContext Probe = new() { ObjectId = "grantlatchprobe", Name = "Grant Latch Probe" };

    /// <summary>
    /// Who the re-invoked handler grants in <see cref="SubscribingToTheGrantLeavesTheSubscribersIdentityIntact"/>
    /// — deliberately NOT <see cref="Probe"/>. Probe created the Space, so the real create already
    /// wrote <c>{id}/_Access/grantlatchprobe_Access</c>; re-granting Probe would fault the write with
    /// <c>Node already exists</c> and the property would then be measured on a write that never ran.
    /// (It did, until the error arm was made to propagate: Copilot's review on #4376 caught the
    /// masking and the very next run reported the duplicate — the reason this constant exists.)
    /// </summary>
    private const string GrantSubject = "grantlatchsecond";

    /// <summary>Settled by the negative control's own terminal arm, so no write is in flight at teardown.</summary>
    private readonly AsyncSubject<Unit> negativeControlSettled = new();

    /// <summary>Settled by the sealed handler's terminal arm, for the same reason.</summary>
    private readonly AsyncSubject<Unit> sealedGrantSettled = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => Bootstrap.Bootstrap(builder)
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .ConfigureHub(c => c
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .WithRequestTimeout(TimeSpan.FromSeconds(60)));

    /// <summary>
    /// The end-to-end control, and the reason the seal cannot be adopted carelessly: the grant is
    /// declared <c>FailsCreateOnError</c>, so a grant write that stopped authorising would not
    /// degrade quietly — the Space create itself would fail, and the creator could not put anything
    /// in the Space afterwards. Creating a child is the literal symptom #4061 reports
    /// (<c>Access denied: Create permission required for node '{id}/page'</c>).
    ///
    /// <para>🚨 Both creates run as <see cref="Probe"/>, NOT as the fixture's own identity, and
    /// through <c>access.RunAs</c> rather than an ambient scope — because the cold create decides
    /// its identity at Subscribe, which the assertion helper performs, not here. Run as the fixture
    /// user this would check nothing: that identity is the test circuit's, and a child create it is
    /// separately entitled to would succeed whether or not the creator grant was ever written.
    /// <see cref="Probe"/> holds NOTHING anywhere — it can create a top-level Space because
    /// <c>SpaceAccessRule</c> lets any authenticated identity do that, and it can create a child
    /// ONLY because the grant made it the Space's Admin. That is the ordinary-user contract.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheCreateStillWorksAndTheCreatorOwnsTheSpace()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        await access.RunAs(Probe, () => meshService.CreateNode(new MeshNode(SpaceId)
            {
                Name = "Grant Latch",
                NodeType = SpaceNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new Space(),
            }))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "the creator-Admin grant is part of the create's CONTRACT (FailsCreateOnError), so a "
                + "create that emits at all is the proof that the grant write still authorised as System");

        await access.RunAs(Probe, () => meshService.CreateNode(new MeshNode("page", SpaceId)
            {
                Name = "Page",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.CrossSilo).Emit(
                "an identity that holds NOTHING outside this Space must be able to create a child in "
                + "the Space it just created — the creator grant is the only thing that confers it, "
                + "and its absence is exactly the 'Create permission required' that #4061 reports");
    }

    /// <summary>
    /// The property under test, asserted SYNCHRONOUSLY on the subscribing thread: subscribing to the
    /// handler's grant pipeline must hand that thread back the identity it subscribed with.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task SubscribingToTheGrantLeavesTheSubscribersIdentityIntact()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var handler = Mesh.ServiceProvider.GetServices<INodePostCreationHandler>()
            .Single(h => h.NodeType.Equals(SpaceNodeType.NodeType, StringComparison.OrdinalIgnoreCase));

        var space = await access.RunAs(Probe, () => meshService.CreateNode(new MeshNode(SpaceId)
            {
                Name = "Grant Latch",
                NodeType = SpaceNodeType.NodeType,
                State = MeshNodeState.Active,
                Content = new Space(),
            }))
            .Should().Within(TestTimeouts.CrossSilo).Emit("the Space the grants below are written under");

        // 🚨 Both subscriptions stay ALIVE past the assertion — disposing one here would cancel the
        // cross-hub write it just started, and the settle-arms below would then wait on a write
        // nobody is running any more. They are disposed in the `finally`, which also covers the
        // path this test EXISTS to take on unfixed code: the property assertion throws, and
        // without the finally both writes would still be subscribed at fixture teardown.
        IDisposable? negativeControl = null;
        IDisposable? sealedGrant = null;
        try
        {
            using (access.SwitchAccessContext(Probe))
            {
                // ── THE NEGATIVE CONTROL, first. The SAME cross-hub write, written the way the site
                // used to be written, subscribed on THIS thread. It has to latch. If it did not, the
                // inner observable would be completing synchronously — the resource would be disposed
                // before Subscribe returned — and the assertion below would be passing for a reason
                // that has nothing to do with the seal. This is the step that makes the test able to
                // fail for the right reason.
                negativeControl = Observable
                    .Using(() => access.ImpersonateAsSystem(),
                        _ => meshService.CreateNode(Grant("negcontrol")))
                    .Subscribe(
                        _ => { },
                        negativeControlSettled.OnError,
                        () => Settle(negativeControlSettled));

                access.Context?.ObjectId.Should().Be(WellKnownUsers.System,
                    "Observable.Using opens the impersonation on the SUBSCRIBING thread and disposes it "
                    + "when the inner observable terminates — a different thread for a cross-hub create — "
                    + "so the subscriber is left holding system-security. If this is not what happened, "
                    + "the write completed synchronously and this fixture cannot see a latch at all, "
                    + $"which would make the assertion below vacuous. It read '{access.Context?.ObjectId ?? "(null)"}'");

                // Undo what the control just demonstrated, so the property is measured from a known state.
                access.SetContext(Probe);

                // ── THE PROPERTY. Same work, same System identity for the write itself, sealed boundary.
                sealedGrant = handler.Handle(space, GrantSubject)
                    .Subscribe(
                        _ => { },
                        sealedGrantSettled.OnError,
                        () => Settle(sealedGrantSettled));

                access.Context?.ObjectId.Should().Be(Probe.ObjectId,
                    "the creator-Admin grant must run as System and hand the subscribing flow back its "
                    + "OWN identity (ImpersonationScopeExtensions.RunAsSystem). Leaving system-security "
                    + "latched is what let MeshExtensions.RunPostCreationHandlersObs announce "
                    + "Admin/Partition/{id} as System on some runs and as the caller — denied — on "
                    + $"others, which is #4061's intermittency. It read '{access.Context?.ObjectId ?? "(null)"}'");
            }

            // 🚨 Each write must COMPLETE, not merely stop. The error arm forwards the fault to the
            // subject instead of settling it, so a grant that was denied — the failure this whole
            // fixture is about — surfaces here as an errored observable rather than as a tidy
            // "settled" that would let the identity assertions above stand on a write that never
            // landed.
            await negativeControlSettled.Should().Within(TestTimeouts.CrossSilo).Emit(
                "the negative control's write must SUCCEED — it is the proof that the old idiom's "
                + "latch was observed on a write that actually ran");
            await sealedGrantSettled.Should().Within(TestTimeouts.CrossSilo).Emit(
                "the sealed grant's write must SUCCEED — RunAsSystem must still authorise it as "
                + "System, or the seal has traded a leak for a fail-closed grant (#638)");
        }
        finally
        {
            negativeControl?.Dispose();
            sealedGrant?.Dispose();
        }
    }

    private static void Settle(AsyncSubject<Unit> subject)
    {
        subject.OnNext(Unit.Default);
        subject.OnCompleted();
    }

    /// <summary>
    /// The same node shape <c>SpacePostCreationHandler</c> mints, so the negative control exercises
    /// the identical cross-hub write rather than a cheaper stand-in.
    /// </summary>
    private static MeshNode Grant(string user) =>
        new($"{user}_Access", $"{SpaceId}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{user} Access",
            MainNode = SpaceId,
            Content = new AccessAssignment
            {
                AccessObject = user,
                DisplayName = user,
                Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = false }],
            },
        };
}
