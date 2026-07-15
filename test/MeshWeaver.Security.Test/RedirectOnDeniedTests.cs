using System.Threading.Tasks;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Pins the "if no access ⇒ redirect here" resolution. A single
/// <see cref="PartitionAccessPolicy.RedirectOnDenied"/> set at a partition root resolves for EVERY
/// node beneath it (nearest ancestor wins), and returns <c>null</c> where none is configured. This is
/// the reliable, config-driven target the GUI sends a denied viewer to — the loop-guard and the actual
/// navigation live in <c>AreaErrorClassifier.IsSafeRedirect</c> + <c>NamedAreaView</c> (unit-tested
/// separately). No fragile "is the target readable" probe: the target is declared, not guessed.
/// </summary>
public class RedirectOnDeniedTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                new MeshNode("_Policy", "RedirectCourse")
                {
                    NodeType = SecurityCollections.PartitionAccessPolicyNodeType,
                    Content = new PartitionAccessPolicy { RedirectOnDenied = "RedirectCourse/Cover" }
                });

    // Security tests need granular permissions — skip the PublicAdmin seed.
    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    [Fact(Timeout = 20000)]
    public async Task RedirectOnDenied_ResolvesFromTheAncestorPolicy_ForEveryNodeBeneath()
    {
        // The partition root itself.
        await Mesh.GetRedirectOnDenied("RedirectCourse")
            .Should().Match(p => p == "RedirectCourse/Cover");
        // A direct child (a gated module).
        await Mesh.GetRedirectOnDenied("RedirectCourse/Introduction")
            .Should().Match(p => p == "RedirectCourse/Cover");
        // A deep descendant resolves the SAME partition-root policy.
        await Mesh.GetRedirectOnDenied("RedirectCourse/Module/Lesson")
            .Should().Match(p => p == "RedirectCourse/Cover");
    }

    [Fact(Timeout = 20000)]
    public async Task RedirectOnDenied_IsNull_WhereNoPolicyConfigured()
        => await Mesh.GetRedirectOnDenied("SomeOtherPartition/Node")
            .Should().Match(p => p == null);
}
