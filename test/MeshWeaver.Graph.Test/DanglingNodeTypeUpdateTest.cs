using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
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
/// 🚨 <b>UPDATE MAY NOT CREATE A NODE WHOSE NODETYPE RESOLVES TO NOTHING</b> — issue #2993, hole A.
///
/// <para>The CREATE path has always refused an unregistered NodeType. The UPDATE paths refused
/// nothing, so <c>update</c> was a supported route to produce an instance with no per-node hub: it
/// does not error, it reads as <c>Unavailable</c> on a timeout, renders empty, and never reaches a
/// verdict — with nothing anywhere naming the type that is missing. Live example on production:
/// <c>rbuergi/_Draft/PartnerRe_EslProposalQA</c> carrying <c>nodeType: EmailDraft</c>.</para>
///
/// <para>Both update verbs are covered here, because they are two different pipelines:
/// <c>IMeshService.UpdateNode</c> (what the MCP <c>update</c> tool calls) runs
/// <see cref="DanglingNodeTypeValidator"/> through <c>NodeUpdatePipeline</c>, while
/// <c>CreateOrUpdateNodeRequest</c> runs no validators at all and carries the same rule inline in
/// <c>MeshExtensions</c>. A guard on one of them is a guard on neither.</para>
///
/// <para>🚨 The counterparty is pinned in the same file: the upsert verb's named escape hatch
/// (<see cref="CreateOrUpdateNodeRequest.AllowUnresolvableNodeType"/>) must still land the write,
/// or every re-import of an already-present cycle member becomes a failure that holds the caller's
/// git baseline — #2556's non-convergent loop, re-created by the fix for #2993.</para>
/// </summary>
public class DanglingNodeTypeUpdateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string MissingType = "No/Such/NodeType";

    private DanglingNodeTypeValidator Guard =>
        Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<DanglingNodeTypeValidator>()
            .Single();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static MeshNode Page(string id, string nodeType) => new(id, TestPartition)
    {
        Name = id,
        NodeType = nodeType,
        State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = $"# {id}\n\npage" },
    };

    private static string NewId() => "dnt" + Guid.NewGuid().ToString("N")[..8];

    // ——— the validator's own decisions ———————————————————————————————————————————————

    [Fact(Timeout = 60000)]
    public async Task AnUpdate_IntroducingAnUnresolvableNodeType_IsRejected()
    {
        var id = NewId();
        var result = await Guard.Validate(new NodeValidationContext
        {
            Operation = NodeOperation.Update,
            Node = Page(id, MissingType),
            ExistingNode = Page(id, "Markdown"),
        }).Should().Within(TestTimeouts.Quick).Emit("the guard must reach a verdict");

        result.IsValid.Should().BeFalse(
            "an update that RETYPES a node to something that resolves to nothing produces an "
            + "instance with no per-node hub — it reads as Unavailable forever and nothing says why");
        result.Reason.Should().Be(NodeRejectionReason.InvalidNodeType);
        result.ErrorMessage.Should().Contain(MissingType,
            "the refusal must NAME the type, or the caller cannot act on it");
    }

    /// <summary>
    /// 🚨 THE REPAIR PATH, and the reason this guard judges a CHANGE rather than a state.
    /// <c>patch</c> refuses <c>nodeType</c> outright (<c>MeshOperations.PatchableFields</c>), so a
    /// full-node <c>update</c> is the only route by which an already-mistyped node can be edited or
    /// fixed at all. A guard that refused every update to such a node would close it.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnUpdate_KeepingTheSameAlreadyDanglingNodeType_IsAllowed()
    {
        var id = NewId();
        var result = await Guard.Validate(new NodeValidationContext
        {
            Operation = NodeOperation.Update,
            Node = Page(id, MissingType),
            ExistingNode = Page(id, MissingType),
        }).Should().Within(TestTimeouts.Quick).Emit("the guard must reach a verdict");

        result.IsValid.Should().BeTrue(
            "round-tripping the type a node ALREADY carries introduces nothing; refusing it would "
            + "make an already-stranded node permanently un-editable, and `patch` cannot write "
            + "nodeType at all");
    }

    [Fact(Timeout = 60000)]
    public async Task AnUpdate_RetypingADanglingNodeToATypeThatResolves_IsAllowed()
    {
        var id = NewId();
        var result = await Guard.Validate(new NodeValidationContext
        {
            Operation = NodeOperation.Update,
            Node = Page(id, "Markdown"),
            ExistingNode = Page(id, MissingType),
        }).Should().Within(TestTimeouts.Quick).Emit("the guard must reach a verdict");

        result.IsValid.Should().BeTrue(
            "naming a type that DOES resolve is the sanctioned repair for a mistyped node — the "
            + "guard exists to preserve that route, not to close it");
    }

    /// <summary>
    /// A guard nobody registered is a guard that does not run. <c>AddGraph</c> wires it; without
    /// this fact the three above would keep passing while production refused nothing.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void TheGuard_IsWiredIntoTheLiveUpdatePipeline()
    {
        var validators = Mesh.ServiceProvider.GetServices<INodeValidator>().ToList();
        validators.OfType<DanglingNodeTypeValidator>().Should().ContainSingle(
            "AddGraph must register the guard, or IMeshService.UpdateNode runs without it");
        Guard.SupportedOperations.Should().Contain(NodeOperation.Update);
        typeof(IOwnerEnforcedNodeValidator).IsInstanceOfType(Guard).Should().BeFalse(
            "NodeUpdatePipeline SKIPS owner-enforced validators client-side — marking this one "
            + "would mean the update verb never runs it");
    }

    // ——— IMeshService.UpdateNode (the MCP `update` route) ————————————————————————————

    [Fact(Timeout = 180000)]
    public async Task UpdateNode_RetypingToAnUnresolvableType_Throws()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(Page(id, "Markdown")).Take(1)
            .Should().Within(60.Seconds()).Emit("the node to retype must exist first");

        var live = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(60.Seconds()).Await();

        var failure = await Record.ExceptionAsync(() =>
            MeshService.UpdateNode(live with { NodeType = MissingType })
                .Take(1).Timeout(60.Seconds()).Await());

        failure.Should().BeOfType<InvalidOperationException>(
            "a refused NodeType is an integrity failure, not a permission one — mapping it to "
            + "UnauthorizedAccessException would send the caller looking for a grant");
        failure!.Message.Should().Contain(MissingType);

        var after = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(60.Seconds()).Await();
        after.NodeType.Should().Be("Markdown",
            "the refusal must leave the node as it was — a half-applied retype is the orphan "
            + "condition this guard exists to prevent");
    }

    // ——— CreateOrUpdateNodeRequest (the upsert verb every importer/installer uses) ————

    [Fact(Timeout = 180000)]
    public async Task Upsert_RetypingToAnUnresolvableType_IsRefused()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(Page(id, "Markdown")).Take(1)
            .Should().Within(60.Seconds()).Emit("the node to retype must exist first");

        var response = await Upsert(Page(id, MissingType), allowUnresolvable: false);

        response.Success.Should().BeFalse(
            "the upsert verb runs NO INodeValidator, so the same rule has to be applied inline — "
            + "otherwise every importer, installer, webhook and node-copy can still write a "
            + "dangling type");
        response.RejectionReason.Should().Be(NodeUpsertRejectionReason.InvalidNodeType);
        response.Error.Should().Contain(MissingType);

        var after = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null).FirstAsync().Timeout(60.Seconds()).Await();
        after.NodeType.Should().Be("Markdown");
    }

    /// <summary>
    /// 🚨 <b>THE COUNTERPARTY.</b> The importer's ordering escape hatch must keep working: a node
    /// that ALREADY EXISTS and is retyped to something this pass cannot put in place first (a
    /// cycle member, a type from another repo) is written anyway, on request, by name. Refusing it
    /// instead would count as a per-file failure, and <c>Failed &gt; 0</c> holds the caller's git
    /// baseline — one cyclic pair would freeze every later commit of the repo, which is exactly the
    /// non-convergent loop #2556 removed.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task Upsert_WithTheNamedImportEscapeHatch_StillLands()
    {
        var id = NewId();
        var path = $"{TestPartition}/{id}";
        await MeshService.CreateNode(Page(id, "Markdown")).Take(1)
            .Should().Within(60.Seconds()).Emit("the node to retype must exist first");

        var response = await Upsert(Page(id, MissingType), allowUnresolvable: true);

        response.Success.Should().BeTrue(
            $"AllowUnresolvableNodeType is the import ordering escape hatch — refusing it turns "
            + $"every re-import of an already-present cycle member into a baseline-holding failure. "
            + $"Error was: {response.Error}");
        // 🚨 Asserted off the response's own node, deliberately NOT off a re-read of the path. The
        // node is now in exactly the stranded state this issue is about, and reading it back would
        // take the 30 s NodeTypeEnrichmentHelpers.SlowPathTimeout before the error overlay
        // activates — i.e. the test would be measuring the symptom, slowly, instead of the write.
        response.Node!.NodeType.Should().Be(MissingType,
            "the escape hatch must actually WRITE the type — a bypass that silently no-ops is the "
            + "same failure as a refusal, only harder to see");

        // The repair, at the same verb: retyping BACK to something that resolves is allowed with no
        // bypass at all. This is the route #2993 says must stay open (`patch` cannot write nodeType),
        // and it leaves the fixture healthy rather than stranded.
        var repaired = await Upsert(Page(id, "Markdown"), allowUnresolvable: false);
        repaired.Success.Should().BeTrue(
            $"retyping a stranded node to a type that DOES resolve is the sanctioned repair. "
            + $"Error was: {repaired.Error}");
        repaired.Node!.NodeType.Should().Be("Markdown");
        Output.WriteLine($"repaired {path} back to Markdown");
    }

    private async Task<CreateOrUpdateNodeResponse> Upsert(MeshNode node, bool allowUnresolvable)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        // 🚨 ObserveNodeOperation, never Mesh.Observe: the test base's Mesh IS the router, and a
        // node-CRUD request issued from it runs the write on the router's own action block — the
        // shape RouterAsTestRequestOriginRatchetGuard exists to stop. This posts from a client hub
        // aimed at the mesh's nodeops execution hub, exactly as MeshService does in production.
        var response = await access
            .RunAsSystem(() => ObserveNodeOperation(
                new CreateOrUpdateNodeRequest(node)
                {
                    AllowUnresolvableNodeType = allowUnresolvable,
                }))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(90.Seconds()).Await();
        Output.WriteLine(
            $"upsert allowUnresolvable={allowUnresolvable} success={response.Success} "
            + $"reason={response.RejectionReason} error={response.Error}");
        return response;
    }
}
