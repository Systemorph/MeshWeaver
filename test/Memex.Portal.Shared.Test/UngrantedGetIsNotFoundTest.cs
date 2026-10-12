#pragma warning disable CS1591

using System.Reactive.Linq;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b><c>get</c> decides access for the CALLER, whichever thread its read finally runs on</b>
/// (MeshWeaver#6570, found under #6564).
///
/// <para><b>What production showed.</b> On a deployed portal an MCP <c>get</c> returned
/// <c>AccessAssignment</c>, <c>Code</c> and <c>NodeType</c> nodes under a sample subtree whose
/// <c>_Access</c> holds no grant the caller matches — <c>whoami</c> at the same paths answered
/// <c>permissions: []</c> and <c>search</c> hid them. Every leaking path carried an interior
/// <c>Data</c> segment (<c>…/samples/Graph/Data/Type/_Access/…</c>), and <c>Data</c> is a
/// unified-reference keyword, so <see cref="MeshOperations.Get"/> first tried the path as a
/// unified read (<c>GetDataRequest</c> to the address before the keyword), and only on that read's
/// error fell back to the node read. The node read ran INSIDE the continuation of the unified
/// response, and <c>FetchNode</c> captured "the caller" from the ambient <c>AccessContext</c> AT
/// THAT MOMENT — the thread delivering the response, not the request. Whatever identity that thread
/// carried (a hub principal, which the stream cache's read gate deliberately passes through) was
/// what the gate evaluated: no error, no log line, the node served.</para>
///
/// <para><b>How this reproduces it without a cluster.</b> The observable is BUILT under the
/// caller's identity and SUBSCRIBED under a hub principal — the shape of "the continuation runs on
/// a different identity than the call". Before the fix the denied caller received the
/// AccessAssignment; after it, the answer is the one an absent path gets.</para>
///
/// <para><b>Controls.</b> The holder of the grant, under the same build/subscribe split, still
/// reads the node (so the denial is the gate's, not a read that cannot succeed); a path WITHOUT a
/// unified keyword stays denied.</para>
/// </summary>
public class UngrantedGetIsNotFoundTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Granted = "GetGateGranted";
    private const string Hidden = "GetGateHidden";
    private const string ViewerUser = "getgate-viewer";
    private const string HolderUser = "getgate-holder";

    /// <summary>The subtree the viewer holds nothing on; <c>Data</c> is a unified-reference keyword.</summary>
    private const string TypeScope = Hidden + "/samples/Data/Type";
    private const string AssignmentPath = TypeScope + "/_Access/" + HolderUser + "_Access";
    private const string AbsentPath = TypeScope + "/_Access/nobody_Access";

    /// <summary>The same subtree shape without a unified keyword.</summary>
    private const string PlainScope = Hidden + "/Plain";
    private const string PlainAssignmentPath = PlainScope + "/_Access/" + HolderUser + "_Access";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter grants Public the Admin role in every
    // default partition, under which the viewer would read everything and the denial would be vacuous.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Granted) { Name = "Granted", NodeType = "Markdown" },
                new MeshNode(Hidden) { Name = "Hidden", NodeType = "Markdown" },
                new MeshNode("Type", Hidden + "/samples/Data") { Name = "Type", NodeType = "Markdown" },
                new MeshNode("Plain", Hidden) { Name = "Plain", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(ViewerUser, "Viewer", Granted),
                AssignmentNodeFactory.UserRole(HolderUser, "Viewer", TypeScope),
                AssignmentNodeFactory.UserRole(HolderUser, "Viewer", PlainScope));

    private IMessageHub SessionHub() => SessionHubFactory.Resolve(
        Mesh, "mcp", "getgate",
        Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(UngrantedGetIsNotFoundTest)));

    /// <summary>
    /// Builds the <c>get</c> as <paramref name="userId"/>, then subscribes it while the ambient
    /// identity is a hub principal — the continuation-on-another-identity shape.
    /// </summary>
    private Task<string> GetBuiltAsSubscribedAsHub(string userId, string path)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetContext(new AccessContext { ObjectId = userId, Name = userId });
        var read = new MeshOperations(SessionHub()).Get(path);
        using (access.ImpersonateAsHub(Mesh))
            return read.Timeout(Budget).Await(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnUngrantedCaller_GetsTheAbsentAnswer_ForAnAccessAssignment_UnderAUnifiedKeywordPath()
    {
        var denied = await GetBuiltAsSubscribedAsHub(ViewerUser, AssignmentPath);
        var absent = await GetBuiltAsSubscribedAsHub(ViewerUser, AbsentPath);

        denied.Should().NotContain("AccessAssignment",
            "the viewer holds no Read on the subtree — who holds which grant must not be disclosed");
        denied.Should().Be(absent,
            "a refused read answers exactly what an absent node answers, so a denial discloses nothing");
    }

    [Fact]
    public async Task TheGrantHolder_StillReadsIt_UnderTheSameSplit()
    {
        var answer = await GetBuiltAsSubscribedAsHub(HolderUser, AssignmentPath);

        answer.Should().Contain("\"nodeType\":\"AccessAssignment\"",
            "the holder's own grant covers the subtree — if this fails, the denial above proves nothing");
    }

    [Fact]
    public async Task APathWithoutAUnifiedKeyword_IsDeniedToo()
    {
        var answer = await GetBuiltAsSubscribedAsHub(ViewerUser, PlainAssignmentPath);

        answer.Should().StartWith("Not found",
            "a refused read of a plain path answers like an absent node");
    }

    /// <summary>A unified read that SUCCEEDS for the holder — the direct-success branch, which posts
    /// its <c>GetDataRequest</c> at subscribe time and never reaches the node-read fallback.</summary>
    private const string PlainUnifiedPath = PlainScope + "/data/";

    /// <summary>The MCP area route of the same node.</summary>
    private const string PlainAreaPath = PlainScope + "/area/";

    [Fact]
    public async Task AUnifiedRead_IsDecidedForTheCaller_NotTheSubscriber()
    {
        var denied = await GetBuiltAsSubscribedAsHub(ViewerUser, PlainUnifiedPath);
        var held = await GetBuiltAsSubscribedAsHub(HolderUser, PlainUnifiedPath);

        held.Should().NotStartWith("Error",
            "the holder reads the node's data — and the read must be decided for HER, not for the hub that subscribed");
        denied.Should().StartWith("Error",
            "the viewer holds no Read on the node, so its unified read is refused");
        denied.Should().Contain(ViewerUser, "the refusal is decided for the caller, never for the subscribing hub");
    }

    [Fact]
    public async Task TheAreaRoute_IsRenderedForTheCaller_NotTheSubscriber()
    {
        var denied = await GetBuiltAsSubscribedAsHub(ViewerUser, PlainAreaPath);
        var held = await GetBuiltAsSubscribedAsHub(HolderUser, PlainAreaPath);

        held.Should().Contain("\"areas\"", "the holder's render is the settled control tree — the control for the denial below");
        denied.Should().NotContain("\"areas\"",
            "the viewer holds no Read on the node, so nothing of its rendered area reaches her");
    }
}
