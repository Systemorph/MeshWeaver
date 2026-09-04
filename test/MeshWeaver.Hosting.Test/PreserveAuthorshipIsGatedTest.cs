using System;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <c>CopyNodeRequest.PreserveAuthorship</c> mints a node whose <see cref="MeshNode.CreatedBy"/>
/// names somebody else, and <c>AccessContextScope.FromNode</c> impersonates exactly that identity —
/// so the flag is gated on the entitlement of the operation it exists for: <see cref="Permission.Delete"/>
/// on the source's namespace, which is what <c>MoveNodePermissionAttribute</c> requires of a mover.
///
/// <para>🚨 The subject is an <b>Editor</b>, and that is the whole design of this test. Editor is
/// <c>Read | Create | Update | …</c> — everything a copy needs and no <c>Delete</c> — so it is the
/// one caller for whom the gate can actually decide something: row-level security lets them read the
/// source and create at the target, the copy would otherwise go through, and only the gate stands
/// between them and a node in their own space that claims the admin as its author. A subject who
/// simply cannot read the source would be stopped by RLS, and a gate exercised only through that
/// case would be a gate that never fires where it matters.</para>
///
/// <para>This suite therefore builds its mesh from <see cref="MonolithMeshTestBase.ConfigureMeshBase"/>
/// — the default one grants Public → Admin, under which every identity holds Delete and no refusal is
/// observable.</para>
/// </summary>
public class PreserveAuthorshipIsGatedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string EditorId = "editor-e";

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static readonly AccessContext EditorContext =
        new() { ObjectId = EditorId, Name = "Editor E" };

    /// <summary>
    /// Base mesh + ONE seeded grant: <see cref="EditorId"/> is an Editor on <c>TestData</c>. No
    /// <c>PublicAdminAccess</c> — see the class remarks.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode($"{EditorId}_Access", $"{TestPartition}/_Access")
            {
                NodeType = "AccessAssignment",
                Name = "Editor E — Editor on TestData",
                MainNode = TestPartition,
                Content = new AccessAssignment
                {
                    AccessObject = EditorId,
                    DisplayName = "Editor E",
                    Roles = [new RoleAssignment { Role = "Editor" }],
                },
            });

    [Fact(Timeout = 60000)]
    public async Task AnEditorWhoCouldNotMoveTheNode_IsRefusedAPreservingCopy()
    {
        var sourcePath = await SeedAsAdmin("gated");

        Access.SetCircuitContext(EditorContext);
        var response = await ObserveNodeOperation(
                new CopyNodeRequest(sourcePath, NewPath("gated-copy"))
                {
                    PreserveAuthorship = true,
                })
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine($"refusal: success={response.Message.Success} reason={response.Message.RejectionReason} error={response.Message.Error}");

        response.Message.Success.Should().BeFalse(
            "an Editor holds no Delete on the namespace, so they could not have MOVED this node — "
            + "preserving its authorship on a copy would hand them an impersonation they cannot "
            + "otherwise obtain");
        response.Message.RejectionReason.Should().Be(NodeCopyRejectionReason.Unauthorized,
            "the refusal is an authorisation decision about the flag, not a missing source");
    }

    /// <summary>
    /// The control arm — without it the test above is satisfied by an Editor who simply cannot copy
    /// at all, and the gate would be indistinguishable from RLS refusing the whole operation.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TheSameEditor_CopiesFreely_WhenTheyDoNotAskToPreserveAuthorship()
    {
        var sourcePath = await SeedAsAdmin("plain");
        var targetPath = NewPath("plain-copy");

        Access.SetCircuitContext(EditorContext);
        var response = await ObserveNodeOperation(new CopyNodeRequest(sourcePath, targetPath))
            .Should().Within(TestTimeouts.Convergence).Emit();

        Output.WriteLine($"plain copy: success={response.Message.Success} reason={response.Message.RejectionReason} error={response.Message.Error}");

        response.Message.Success.Should().BeTrue(
            response.Message.Error
            ?? "the gate is about the flag alone — an Editor may still copy, and the copy is stamped for them");

        var copy = await ReadNode(targetPath).Should().Within(TestTimeouts.Convergence)
            .Match(n => n is not null, "the copy must exist");
        copy!.CreatedBy.Should().Be(EditorId,
            "a copy is a new node, stamped for whoever made it — which is exactly why it needs no "
            + "Delete on the source");
    }

    private static string NewPath(string prefix) =>
        $"{TestPartition}/{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    private async Task<string> SeedAsAdmin(string prefix)
    {
        Access.SetCircuitContext(TestUsers.Admin);
        var path = NewPath(prefix);
        await NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            Name = prefix,
            NodeType = "Markdown",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();
        return path;
    }
}
