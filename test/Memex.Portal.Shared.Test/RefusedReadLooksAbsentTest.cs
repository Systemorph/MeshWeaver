#pragma warning disable CS1591

using System.Reactive.Linq;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A refused read answers exactly what an absent path answers</b> on the caller-facing
/// read surfaces beyond <c>get</c> (which #6571 settled): <c>render_area</c> (and the
/// <c>get {path}/area/…</c> route built on it) and <c>content/list</c>.
///
/// <para>Before the fix both handed the owner's refusal on verbatim — <c>"Error: Access denied:
/// user '…' lacks Read permission on '{path}'"</c> — while an absent path answered
/// <c>"Not found: …"</c> / <c>"Error: no matching node …"</c>. The difference alone told a caller
/// who may not read a node that one exists at that exact path.</para>
///
/// <para>The comparison is made with the path masked, against TWO absent shapes: a path under the
/// same hidden parent, and a path under a root that does not exist. The grant holder renders the
/// node — the positive control.</para>
/// </summary>
public class RefusedReadLooksAbsentTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Hidden = "RefusalShapeHidden";
    private const string ViewerUser = "refusal-viewer";
    private const string HolderUser = "refusal-holder";
    private const string SecretName = "Obsidianfern";
    private const string SecretPath = Hidden + "/" + SecretName;
    private const string AbsentSibling = Hidden + "/NoSuchSibling";
    private const string AbsentRoot = "RefusalShapeNoSuchRoot/" + SecretName;

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter grants Public the Admin role in every
    // default partition, under which the viewer would read everything and the denial would be vacuous.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Hidden) { Name = "Hidden", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(HolderUser, "Viewer", Hidden));

    /// <summary>The secret node is WRITTEN, not declared — a declared node is a configuration node,
    /// served without the row filter (see <c>Doc/Architecture/AccessControl</c>).</summary>
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
                .CreateNode(new MeshNode(SecretName, Hidden) { Name = SecretName, NodeType = "Markdown" }))
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
    }

    private IMessageHub SessionHub() => SessionHubFactory.Resolve(
        Mesh, "mcp", "refusalshape",
        Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(RefusedReadLooksAbsentTest)));

    private Task<string> As(string userId, Func<MeshOperations, IObservable<string>> read)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetContext(new AccessContext { ObjectId = userId, Name = userId });
        return read(new MeshOperations(SessionHub()))
            .Catch((Exception ex) => Observable.Return($"Fault: {ex.GetType().Name}: {ex.Message}"))
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
    }

    private const int RenderBudgetSeconds = 10;

    private Task<string> RenderArea(string userId, string path)
        => As(userId, ops => ops.RenderArea(path, timeoutSeconds: RenderBudgetSeconds));

    private Task<string> ContentList(string userId, string path)
        => As(userId, ops => ops.ContentList(path + "/content"));

    private static string Masked(string answer, string path) => answer.Replace(path, "<path>");

    [Fact]
    public async Task RenderArea_ARefusedNode_AnswersLikeAnAbsentOne()
    {
        var denied = await RenderArea(ViewerUser, SecretPath);
        var sibling = await RenderArea(ViewerUser, AbsentSibling);
        var root = await RenderArea(ViewerUser, AbsentRoot);
        Output.WriteLine($"denied:  {denied}");
        Output.WriteLine($"sibling: {sibling}");
        Output.WriteLine($"root:    {root}");

        denied.ToLowerInvariant().Should().NotContain("access denied",
            "naming the refusal tells the caller a node exists at the path");
        Masked(denied, SecretPath).Should().Be(Masked(sibling, AbsentSibling),
            "a refused render answers exactly what an absent sibling answers");
        Masked(denied, SecretPath).Should().Be(Masked(root, AbsentRoot),
            "a refused render answers exactly what a path under no node answers");
    }

    [Fact]
    public async Task RenderArea_TheGrantHolder_RendersIt()
    {
        var answer = await RenderArea(HolderUser, SecretPath);
        Output.WriteLine($"holder: {answer}");

        answer.Should().Contain(SecretName,
            "the holder's grant covers the node — if this fails, the refusal above proves nothing");
    }

    [Fact]
    public async Task ContentList_ARefusedNode_AnswersLikeAnAbsentOne()
    {
        var denied = await ContentList(ViewerUser, SecretPath);
        var sibling = await ContentList(ViewerUser, AbsentSibling);
        var root = await ContentList(ViewerUser, AbsentRoot);
        Output.WriteLine($"denied:  {denied}");
        Output.WriteLine($"sibling: {sibling}");
        Output.WriteLine($"root:    {root}");

        denied.ToLowerInvariant().Should().NotContain("access denied",
            "naming the refusal tells the caller a node exists at the path");
        Masked(denied, SecretPath + "/content").Should().Be(Masked(sibling, AbsentSibling + "/content"),
            "a refused listing answers exactly what an absent sibling answers");
        Masked(denied, SecretPath + "/content").Should().Be(Masked(root, AbsentRoot + "/content"),
            "a refused listing answers exactly what a path under no node answers");
    }

    [Fact]
    public async Task ContentList_TheGrantHolder_IsServedByTheOwner()
    {
        var answer = await ContentList(HolderUser, SecretPath);
        Output.WriteLine($"holder: {answer}");

        // The plain test mesh configures no content collection, so a served lookup says so.
        answer.Should().Contain("collection 'content' not found",
            "the holder's grant covers the node — if this fails, the refusal above proves nothing");
    }
}
