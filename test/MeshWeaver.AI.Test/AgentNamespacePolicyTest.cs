using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The Agent provider's static read-only policy: an Admin over the <c>Agent</c> namespace is capped
/// to read/execute/api/export by the <c>PartitionAccessPolicy</c> node that provider emits from
/// <c>GetStaticNodes()</c> — never create/update/delete.
///
/// <para>Moved out of <c>MeshWeaver.Security.Test.StaticNamespacePolicyTests</c> (#2276), which
/// pinned the same contract for the Doc, Agent and Role providers together. The contract is
/// general and stays there; each INSTANCE of it belongs with the provider that emits the policy,
/// and MeshWeaver.AI is leaving this repository — a policy test for its provider has to leave with
/// it, or it becomes a test of something the repo no longer contains.</para>
/// </summary>
public class AgentNamespacePolicyTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // NOT opted into ShareMeshAcrossTests — static policy caps differ between fresh and reused
    // service providers, which is why the original class carried the same note.

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddAgentType()
            .AddRowLevelSecurity()
            .AddMeshNodes(AssignmentNodeFactory.UserRole("admin_agent", "Admin", ""));

    [Fact(Timeout = 20000)]
    public async Task AgentNamespace_AdminCappedToReadOnly()
    {
        var expected = Permission.Read | Permission.Execute | Permission.Api | Permission.Export;
        await Mesh.GetEffectivePermissions("Agent/ThreadNamer", "admin_agent")
            .Should().Match(p => p == expected, "Agent namespace has a static read-only policy");
    }
}
