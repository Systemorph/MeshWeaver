using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>OCCUPANCY IS NOT REGISTRATION</b> — Systemorph/MeshWeaver#5008 (and its duplicate #2231).
///
/// <para>Every WRITE boundary asked "is a NodeType registered at this path?" and answered it with
/// <c>IStorageAdapter.Exists</c> — occupancy alone. The ACTIVATION boundary has asked the content
/// question since #2245. So a write naming a path that something ELSE occupies was accepted and
/// then refused on activation, with a bare
/// <c>As&lt;NodeTypeDefinition&gt; for Feedback: value is PluginContent</c> line as its only
/// trace.</para>
///
/// <para>🚨 <b>The row still reads — the HUB is what is wrong</b>, and this is worth stating
/// because the sibling #2993 case (a type resolving to NOTHING) really does leave a node with no
/// hub at all. Measured read-only on the live mesh: the stranded production instance returns its
/// full content through an ordinary read, while its activation logs <i>"path 'Feedback' is
/// occupied by a node that is not a NodeType declaration … applying error overlay"</i>. So the
/// page serves the diagnostic instead of the type's views and typed requests are NACKed; the data
/// is not lost.</para>
///
/// <para>The production shape, measured read-only on memex.meshweaver.cloud: a Store plugin root
/// sits at the bare path <c>Feedback</c> while its declaration is <c>Feedback/Feedback</c>. Nine
/// of nine legitimate instances across 117 readable partitions name the QUALIFIED path; exactly
/// one names the bare one, and it is the instance the incident's own log line quotes. So refusing
/// the bare form costs nothing that works today — which is what
/// <see cref="AnInstanceOfARealDeclaration_IsStillAccepted"/> pins, and without that control this
/// change would be a plausible way to break every plugin in the mesh.</para>
///
/// <para>🚨 A refusal at the write boundary stops NEW strandings; it does nothing for the ones
/// already persisted, whose activations keep firing the same fingerprint — which is why #2231 was
/// closed and mechanically reopened three times. That half lives in
/// <c>NodeTypeOccupiedPathActivationTest</c>, on the route this class cannot reach: a monolith mesh
/// answers the existence probe, so every activation here is caught by <c>ProbeCollision</c> before
/// the slow path is entered, and an activation assertion in this class would pass with or without
/// the change.</para>
/// </summary>
public class NodeTypePathOccupancyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private INodeTypeDeclarationProbe Probe =>
        Mesh.ServiceProvider.GetRequiredService<INodeTypeDeclarationProbe>();

    private static string NewId() => "occ" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A node that is PROVABLY not a declaration — the test's stand-in for the Store/Plugin
    /// root: a non-empty NodeType that is not <c>NodeType</c>, and content typed as something
    /// else.</summary>
    private static MeshNode Occupant(string id) => new(id, TestPartition)
    {
        Name = id,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = "# not a declaration" },
    };

    /// <summary>A real NodeType declaration, the shape every legitimate type carries.</summary>
    private static MeshNode Declaration(string id) => new(id, TestPartition)
    {
        Name = id,
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        Content = new NodeTypeDefinition { Description = "a declaration with no compile lifecycle" },
    };

    private static MeshNode Instance(string id, string nodeType) => new(id, TestPartition)
    {
        Name = id,
        NodeType = nodeType,
        State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {id}" },
    };

    // ——— the predicate itself, one-sided in both directions ——————————————————————————

    /// <summary>
    /// 🚨 <b>The control on the OTHER side of the change.</b> The test may only ever convict a node
    /// it can PROVE is not a declaration; a false positive refuses a write that would have worked,
    /// and the mesh has no way back from that. Each of these three is a shape the incident's
    /// predicate must clear, and each is a real population: built-in declarations that leave
    /// <c>NodeType</c> unset (Role, Group), declarations that say <c>NodeType</c> (all nine live
    /// Feedback instances' target), and content that arrives as untyped JSON on a hub whose
    /// registry does not know the discriminator.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void TheProbe_ConvictsOnlyWhatItCanProve()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        Probe.DescribeNonDeclaration(Occupant("w1")).Should().NotBeNull(
            "a node with a non-empty NodeType that is not 'NodeType', carrying content typed as "
            + "something else entirely, is the incident's shape and the only thing this may convict");

        Probe.DescribeNonDeclaration(Declaration("w2")).Should().BeNull(
            "a declaration says NodeType='NodeType' — convicting it would refuse every instance of "
            + "every type in the mesh");

        Probe.DescribeNonDeclaration(new MeshNode("w3", TestPartition)
        {
            Name = "w3",
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition(),
        }).Should().BeNull(
            "an UNSET NodeType proves nothing — several built-in declarations (Role, Group) leave "
            + "it unset, and this test is only ever allowed to say 'definitely not'");

        Probe.DescribeNonDeclaration(new MeshNode("w4", TestPartition)
        {
            Name = "w4",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = System.Text.Json.JsonSerializer.SerializeToElement(new { description = "x" }),
        }).Should().BeNull(
            "untyped JSON deserialises into a NodeTypeDefinition happily, so it is NOT proof of "
            + "anything — the degraded-content shape must fall through, not be convicted");
    }

    /// <summary>
    /// The refusal has to NAME what is in the way. "NodeType 'X' is not registered" sends the
    /// reader off to create a node that is already sitting at that path — which is how #2231 read
    /// as a lookup-ordering puzzle for a month.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void TheConviction_NamesTheOccupantAndItsType()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var occupant = Occupant("w5");
        var described = Probe.DescribeNonDeclaration(occupant);
        described.Should().Contain(occupant.Path).And.Contain("Markdown",
            "both sides of the collision, or the operator has to open an incident to learn which "
            + "node is in the way");
    }

    /// <summary>
    /// A seam nobody registered answers nothing, and because the test is one-sided an absent probe
    /// convicts NOTHING — i.e. the whole fix silently reverts to the old behaviour. That is the
    /// failure mode this fact exists to make impossible.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void TheProbe_IsWiredIntoTheLiveMesh()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Mesh.ServiceProvider.GetService<INodeTypeDeclarationProbe>().Should().NotBeNull(
            "AddGraph must register it, or NodeTypeResolution falls back to occupancy-only on "
            + "every write boundary with nothing to show for it");
    }

    // ——— the three write boundaries ————————————————————————————————————————————————————

    /// <summary>
    /// 🚨 <b>The positive control.</b> On the unfixed code this create SUCCEEDS — the create path
    /// asks <c>Exists(typePath)</c>, the occupant is there, and the instance lands unreadable. This
    /// is the exact write that produced
    /// <c>rbuergi/Feedback/20260920-1207-install-record-partition-test-nondeterministic</c>.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task Create_NamingAnOccupiedPathAsItsNodeType_IsRefused()
    {
        var occupant = Occupant(NewId());
        await MeshService.CreateNode(occupant).Take(1)
            .Should().Within(60.Seconds()).Emit("the occupant must exist before it can collide",
                cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(Instance(NewId(), occupant.Path))
                .Take(1).Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken));

        failure.Should().NotBeNull(
            "a node exists at that path, but it is not a NodeType declaration — accepting the "
            + "write produces an instance that binds the OCCUPANT's hub configuration and serves "
            + "an error overlay instead of its type's views");
        failure!.Message.Should().Contain(occupant.Path);
        failure.Message.Should().Contain("Markdown",
            "the refusal must name WHAT is in the way, not merely that something is");
        Output.WriteLine($"create refused: {failure.Message}");
    }

    /// <summary>
    /// 🚨 <b>The control on the other side, at the boundary.</b> Nine of nine live Feedback
    /// instances name a real declaration's path. If this fact ever fails, the change has stopped
    /// being a fix and become an outage.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task AnInstanceOfARealDeclaration_IsStillAccepted()
    {
        var declaration = Declaration(NewId());
        await MeshService.CreateNode(declaration).Take(1)
            .Should().Within(60.Seconds()).Emit("the declaration must land first",
                cancellationToken: TestContext.Current.CancellationToken);

        await MeshService.CreateNode(Instance(NewId(), declaration.Path)).Take(1)
            .Should().Within(60.Seconds()).Emit(
                "an instance of a REAL declaration is the normal case and must be untouched by the "
                + "occupancy test",
                cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// <c>IMeshService.UpdateNode</c> — the route the MCP <c>update</c> tool takes, guarded by
    /// <see cref="DanglingNodeTypeValidator"/>. A rule applied at one of the three boundaries and
    /// not the others is the drift <c>NodeTypeResolution</c> exists to prevent.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task Update_RetypingToAnOccupiedPath_IsRefused()
    {
        var occupant = Occupant(NewId());
        await MeshService.CreateNode(occupant).Take(1)
            .Should().Within(60.Seconds()).Emit("the occupant must exist first",
                cancellationToken: TestContext.Current.CancellationToken);

        var guard = Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<DanglingNodeTypeValidator>().Single();
        var id = NewId();
        var result = await guard.Validate(new NodeValidationContext
        {
            Operation = NodeOperation.Update,
            Node = Instance(id, occupant.Path),
            ExistingNode = Instance(id, "Markdown"),
        }).Should().Within(TestTimeouts.Convergence).Emit("the guard must reach a verdict",
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse(
            "update was the boundary #2993 closed for a MISSING type; a type path that is OCCUPIED "
            + "strands the instance exactly the same way");
        result.Reason.Should().Be(NodeRejectionReason.InvalidNodeType);
        result.ErrorMessage.Should().Contain(occupant.Path);
    }

    /// <summary>
    /// <c>CreateOrUpdateNodeRequest</c> — the verb every importer, installer, webhook and node-copy
    /// uses. It runs NO validators, so it carries the rule inline; a guard on the other two is a
    /// guard on neither.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task Upsert_RetypingToAnOccupiedPath_IsRefused()
    {
        var occupant = Occupant(NewId());
        await MeshService.CreateNode(occupant).Take(1)
            .Should().Within(60.Seconds()).Emit("the occupant must exist first",
                cancellationToken: TestContext.Current.CancellationToken);

        var id = NewId();
        await MeshService.CreateNode(Instance(id, "Markdown")).Take(1)
            .Should().Within(60.Seconds()).Emit("the node to retype must exist first",
                cancellationToken: TestContext.Current.CancellationToken);

        var response = await Upsert(Instance(id, occupant.Path), allowUnresolvable: false);

        response.Success.Should().BeFalse(
            $"the upsert runs no INodeValidator, so the occupancy rule has to be inline. "
            + $"Error was: {response.Error}");
        response.RejectionReason.Should().Be(NodeUpsertRejectionReason.InvalidNodeType);
        response.Error.Should().Contain(occupant.Path);
    }

    /// <summary>
    /// 🚨 <b>The counterparty.</b> The importer's named escape hatch
    /// (<c>AllowUnresolvableNodeType</c>) must keep writing — refusing it turns every re-import of
    /// an already-present cycle member into a baseline-holding failure (#2556). It is also the only
    /// way a stranded instance can still be MADE, which is what
    /// <c>NodeTypeOccupiedPathActivationTest</c> needs to exist.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task Upsert_WithTheNamedImportEscapeHatch_StillLandsOnAnOccupiedPath()
    {
        var occupant = Occupant(NewId());
        await MeshService.CreateNode(occupant).Take(1)
            .Should().Within(60.Seconds()).Emit("the occupant must exist first",
                cancellationToken: TestContext.Current.CancellationToken);

        var id = NewId();
        await MeshService.CreateNode(Instance(id, "Markdown")).Take(1)
            .Should().Within(60.Seconds()).Emit("the node to strand must exist first",
                cancellationToken: TestContext.Current.CancellationToken);

        var stranded = await Upsert(Instance(id, occupant.Path), allowUnresolvable: true);
        stranded.Success.Should().BeTrue(
            $"the occupancy test must narrow the DEFAULT rule, never the named bypass. "
            + $"Error was: {stranded.Error}");
        // 🚨 Asserted off the RESPONSE's own node, deliberately not off a re-read — the node is now
        // in exactly the stranded state, and reading it back would measure the symptom rather than
        // the write. Same reasoning as DanglingNodeTypeUpdateTest's counterparty fact.
        stranded.Node!.NodeType.Should().Be(occupant.Path,
            "a bypass that silently no-ops is the same failure as a refusal, only harder to see");

        // 🚨 NO repair step here, and that is deliberate. Retyping recycles the node's hub
        // (NodeTypeRebindWatcher, #1104), so a second write issued straight after the first races
        // that teardown and is refused with "Hub … is shutting down (RunLevel=DisposeHostedHubs)"
        // — measured once in a full-project run and not in the isolated one, i.e. a timing race in
        // the TEST, not a verdict about the product. The repair route itself is pinned where it
        // belongs, by DanglingNodeTypeUpdateTest.AnUpdate_RetypingADanglingNodeToATypeThatResolves_
        // IsAllowed, on a node that is not mid-rebind.
    }

    private async Task<CreateOrUpdateNodeResponse> Upsert(MeshNode node, bool allowUnresolvable)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        // ObserveNodeOperation, never Mesh.Observe — see DanglingNodeTypeUpdateTest.Upsert for why
        // (RouterAsTestRequestOriginRatchetGuard).
        var response = await access
            .RunAsSystem(() => ObserveNodeOperation(
                new CreateOrUpdateNodeRequest(node)
                {
                    AllowUnresolvableNodeType = allowUnresolvable,
                }))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(90.Seconds()).Await(TestContext.Current.CancellationToken);
        Output.WriteLine(
            $"upsert allowUnresolvable={allowUnresolvable} success={response.Success} "
            + $"reason={response.RejectionReason} error={response.Error}");
        return response;
    }
}
