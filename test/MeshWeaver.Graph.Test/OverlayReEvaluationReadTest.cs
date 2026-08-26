using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The production wiring of the compilation-overlay re-evaluation
/// (<see cref="NodeTypeEnrichmentHelpers.AuthoritativeTypeRead"/>) — issue #1814 defect B.
///
/// <para>🚨 Why this test exists separately from
/// <c>OverlaySelfHealWatcherTest</c>: that one hands the watcher a re-read lambda and proves the
/// watcher uses it. Every one of its assertions would still pass if the REAL re-read were wired to
/// something that can never answer — which is exactly the failure being fixed, one layer down. So
/// this pins the seam itself: it goes to the mesh's QUERY PROVIDERS (storage), under the System
/// identity, and it answers about the requested path and no other.</para>
///
/// <para>🚨 Drives <see cref="NodeTypeEnrichmentHelpers.AuthoritativeTypeRead"/> against a REAL mesh
/// hub — never a mocked <c>IMessageHub</c>/<c>IMeshQueryCore</c> (Systemorph/MeshWeaver#1810:
/// AGENTS.md forbids mocking either). <see cref="ReadsThroughTheQueryCore_AsSystem_ForTheRequestedPath"/>
/// proves "runs as System" the way that actually matters in production: it reads successfully under
/// an ambient circuit identity that has NO read access to the target partition — a mocked
/// <c>UserId</c> field on a captured request proves the WRONG thing, since the field could be right
/// and RLS still filter the row out (or vice-versa).</para>
/// </summary>
public class OverlayReEvaluationReadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string NodeTypePath = $"{TestPartition}/Plugin";

    // 🚨 ConfigureMeshBase (no PublicAdminAccess seed) — the default test mesh grants Public→Admin
    // everywhere, which would make every identity an administrator and the "runs as System despite
    // the caller having no access" assertion below vacuously true (see GlobalSettingsAccessTest for
    // the same reasoning). RLS has to be genuinely enforced for the positive proof to mean anything.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder);

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private IMeshService MeshService =>
        Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private Task<MeshNode> CreateTypeNode(string id, string ns) =>
        MeshService.CreateNode(new MeshNode(id, ns) { NodeType = MeshNode.NodeTypePath, Version = 3259 })
            .Should().Emit();

    /// <summary>
    /// The read resolves through <c>IMeshQueryCore</c> — the mesh's query-provider fan-out over
    /// storage — and asks for the NodeType's path as System. Not through
    /// <c>GetWorkspace().GetMeshNodeStream(...)</c>, whose cached snapshot is the thing that went
    /// permanently stale.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ReadsThroughTheQueryCore_AsSystem_ForTheRequestedPath()
    {
        await CreateTypeNode("Plugin", TestPartition);

        // A plain signed-in user with NO grant on TestPartition — the identity that would NOT be
        // able to read this node directly (the test base's default identity is an admin, exactly
        // the one that would never catch a missing System-impersonation).
        Access.SetCircuitContext(new AccessContext
        {
            ObjectId = "restricted-reader",
            Name = "restricted-reader",
            Roles = [],
        });
        try
        {
            var read = NodeTypeEnrichmentHelpers.AuthoritativeTypeRead(Mesh, NodeTypePath);
            read.Should().NotBeNull();

            var node = await read!().Should().Emit();

            node.Should().NotBeNull(
                "the re-evaluation is infrastructure — it runs under the System identity like every "
                + "other read on the enrichment path, not under whoever happens to be signed in, so "
                + "it must succeed even though 'restricted-reader' has no grant on this partition");
            node!.Path.Should().Be(NodeTypePath);
        }
        finally
        {
            Access.SetCircuitContext(TestUsers.Admin);
        }
    }

    /// <summary>
    /// A ranked or fuzzy hit for a NEIGHBOURING node is not an answer about this type: acting on it
    /// would recycle an instance against a build it is not waiting for.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ANeighbouringHit_IsNotAnAnswer()
    {
        await CreateTypeNode("PluginContent", TestPartition);
        await CreateTypeNode("Coupon", TestPartition);

        var read = NodeTypeEnrichmentHelpers.AuthoritativeTypeRead(Mesh, NodeTypePath);
        (await read!().Should().Emit()).Should().BeNull();
    }

    /// <summary>
    /// Nothing at the path is "no answer", not "healed" — the watcher's ladder simply asks again.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AnEmptyResult_IsNull_NotAHealSignal()
    {
        var read = NodeTypeEnrichmentHelpers.AuthoritativeTypeRead(Mesh, $"{TestPartition}/NothingHereAtAll");
        (await read!().Should().Emit()).Should().BeNull();
    }
}

/// <summary>
/// The one case that needs a hub which genuinely has NO <c>IMeshQueryCore</c> registered — a bare
/// routing hub (<see cref="HubTestBase"/>, no <c>AddGraph()</c>), not a mock standing in for one.
/// </summary>
public class OverlayReEvaluationReadNoQueryCoreTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// A host with no query core (a bare routing hub) gets NO re-read rather than a fake one — the
    /// watcher then keeps its push-only behaviour instead of pretending to re-evaluate.
    /// </summary>
    [Fact]
    public void NoQueryCore_MeansNoReRead() =>
        NodeTypeEnrichmentHelpers.AuthoritativeTypeRead(Mesh, "Store/Plugin")
            .Should().BeNull();
}
