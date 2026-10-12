#pragma warning disable CS1591

using System.Reactive.Linq;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Data.Completion;
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
/// 🚨 <b>Every caller-facing read decides access for the CALLER captured at its entry, whichever
/// thread it is finally subscribed on</b> (MeshWeaver#6570, the sweep beyond <c>get</c>).
///
/// <para>#6570 found <c>get</c> re-reading "the caller" from the ambient <c>AccessContext</c>
/// inside the continuation of an earlier response, where the identity was the delivering hub's,
/// and the read gates pass hub principals through. This class runs the same probe against the
/// other read surfaces the MCP tools and <c>/api/mesh/*</c> expose: each read is BUILT under an
/// ungranted caller and SUBSCRIBED under a hub principal (the continuation-on-another-identity
/// shape). The ungranted caller must learn nothing a reader learns; the grant holder, under the
/// same split, must still read (the positive control — without it a denial proves nothing).</para>
/// </summary>
public class CallerFacingReadsDecideForTheCallerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Granted = "ReadGateGranted";
    private const string Hidden = "ReadGateHidden";
    private const string ViewerUser = "readgate-viewer";
    private const string HolderUser = "readgate-holder";

    /// <summary>A name no other node in the mesh carries, so finding it anywhere is the leak.</summary>
    private const string SecretName = "Zephyrinequartz";
    private const string SecretPath = Hidden + "/" + SecretName;

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter grants Public the Admin role in every
    // default partition, under which the viewer would read everything and the denial would be vacuous.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Granted) { Name = "Granted", NodeType = "Markdown" },
                new MeshNode(Hidden) { Name = "Hidden", NodeType = "Markdown" },
                AssignmentNodeFactory.UserRole(ViewerUser, "Viewer", Granted),
                AssignmentNodeFactory.UserRole(HolderUser, "Viewer", Hidden));

    /// <summary>
    /// 🚨 The secret node is WRITTEN, not declared with <c>AddMeshNodes</c>: a declared node is a
    /// configuration node, and the static query provider serves those without the row filter (they
    /// are type definitions and built-ins). A probe on one would measure that provider, not the
    /// identity a read is decided for.
    /// </summary>
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
                .CreateNode(new MeshNode(SecretName, Hidden) { Name = SecretName, NodeType = "Markdown" }))
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
    }

    private IMessageHub SessionHub() => SessionHubFactory.Resolve(
        Mesh, "mcp", "readgate",
        Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(CallerFacingReadsDecideForTheCallerTest)));

    /// <summary>
    /// Builds the read as <paramref name="userId"/>, then subscribes it while the ambient identity
    /// is a hub principal — the continuation-on-another-identity shape #6570 measured.
    /// </summary>
    private Task<T> BuiltAsSubscribedAsHub<T>(string userId, Func<IObservable<T>> build)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetContext(new AccessContext { ObjectId = userId, Name = userId });
        var read = build();
        using (access.ImpersonateAsHub(Mesh))
            return read.Timeout(Budget).Await(TestContext.Current.CancellationToken);
    }

    private Task<string> Autocomplete(string userId, string context)
        => BuiltAsSubscribedAsHub(userId, () =>
            Mesh.ServiceProvider.GetRequiredService<IChatCompletionOrchestrator>()
                .GetCompletions("@" + SecretName[..6], context)
                .SelectMany(batch => batch.Items.Select(item => $"{batch.Category}:{item.Category}|{item.Path}|{item.InsertText}|{item.Label}"))
                .ToList()
                .Select(items => string.Join("\n", items)));

    [Fact]
    public async Task Autocomplete_ForAnUngrantedCaller_NamesNothingUnderTheHiddenSubtree()
    {
        var denied = await Autocomplete(ViewerUser, Hidden);
        Output.WriteLine($"viewer: {denied}");

        denied.Should().NotContain(SecretName,
            "the viewer holds no Read under the subtree — a completion naming a node there discloses it");
    }

    [Fact]
    public async Task Autocomplete_TheGrantHolder_StillCompletesIt_UnderTheSameSplit()
    {
        var answer = await Autocomplete(HolderUser, Hidden);
        Output.WriteLine($"holder: {answer}");

        answer.Should().Contain(SecretName,
            "the holder's grant covers the subtree — if this fails, the denial above proves nothing");
    }

    private Task<string> Search(string userId, string query)
        => BuiltAsSubscribedAsHub(userId, () => new MeshOperations(SessionHub()).Search(query, Hidden));

    [Theory]
    [InlineData("Zephyrine")]
    [InlineData("name:Zephyrinequartz")]
    public async Task Search_ForAnUngrantedCaller_FindsNothingUnderTheHiddenSubtree(string query)
    {
        var denied = await Search(ViewerUser, query);
        Output.WriteLine($"viewer: {denied}");

        denied.Should().NotContain(SecretName,
            "the viewer holds no Read under the subtree — a search hit there discloses the node");
    }

    [Theory]
    [InlineData("Zephyrine")]
    [InlineData("name:Zephyrinequartz")]
    public async Task Search_TheGrantHolder_StillFindsIt_UnderTheSameSplit(string query)
    {
        var answer = await Search(HolderUser, query);
        Output.WriteLine($"holder: {answer}");

        answer.Should().Contain(SecretName,
            "the holder's grant covers the subtree — if this fails, the denial above proves nothing");
    }

    /// <summary>
    /// The node's OWN providers answer an <c>AutocompleteRequest</c> (its layout areas, data ids,
    /// files, indexed chunks), so the request is a read of that node. Asked with the caller's real
    /// identity — no thread split at all — an ungranted caller must get nothing from it.
    /// </summary>
    private Task<string> AutocompleteInsideTheNode(string userId)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetContext(new AccessContext { ObjectId = userId, Name = userId });
        return Mesh.ServiceProvider.GetRequiredService<IChatCompletionOrchestrator>()
            .GetCompletions("@Ov", SecretPath)
            .SelectMany(batch => batch.Items)
            .Select(item => $"{item.Path}|{item.InsertText}|{item.Label}")
            .ToList()
            .Select(items => string.Join("\n", items))
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Autocomplete_InsideANodeTheCallerCannotRead_AnswersNothingFromIt()
    {
        var denied = await AutocompleteInsideTheNode(ViewerUser);
        Output.WriteLine($"viewer: {denied}");

        denied.Should().NotContain(SecretPath,
            "the node's providers must not answer a caller holding no Read on it");
    }

    [Fact]
    public async Task Autocomplete_InsideANodeTheHolderCanRead_IsAnsweredByIt()
    {
        var answer = await AutocompleteInsideTheNode(HolderUser);
        Output.WriteLine($"holder: {answer}");

        answer.Should().Contain(SecretPath,
            "the node's own layout areas complete for a reader — if this fails, the denial above proves nothing");
    }

    /// <summary>Inside the test's own budget, so a render the owner refuses answers as a fault the
    /// probe can read instead of racing the outer bound.</summary>
    private const int RenderBudgetSeconds = 10;

    private Task<string> RenderArea(string userId, string path)
        => BuiltAsSubscribedAsHub(userId, () =>
            new MeshOperations(SessionHub()).RenderArea(path, timeoutSeconds: RenderBudgetSeconds)
                .Catch((Exception ex) => Observable.Return($"Fault: {ex.GetType().Name}: {ex.Message}")));

    [Fact]
    public async Task RenderArea_ForAnUngrantedCaller_RendersNothingOfTheNode()
    {
        var denied = await RenderArea(ViewerUser, SecretPath);
        Output.WriteLine($"viewer: {denied}");

        denied.Should().NotContain("\"areas\"",
            "the viewer holds no Read on the node — its rendered area must not reach them");
    }

    [Fact]
    public async Task RenderArea_TheGrantHolder_StillRendersIt_UnderTheSameSplit()
    {
        var answer = await RenderArea(HolderUser, SecretPath);
        Output.WriteLine($"holder: {answer}");

        answer.Should().Contain(SecretName,
            "the holder's grant covers the node — if this fails, the denial above proves nothing");
    }

    /// <summary>The answer only a caller the owner hub served as a READER gets: the plain test mesh
    /// configures no content collection, so a served lookup says the collection is missing.</summary>
    private const string ServedAsReader = "collection 'content' not found";

    private Task<string> ContentList(string userId, string path)
        => BuiltAsSubscribedAsHub(userId, () => new MeshOperations(SessionHub()).ContentList(path));

    [Fact]
    public async Task ContentList_ForAnUngrantedCaller_IsNotServedByTheOwner()
    {
        var denied = await ContentList(ViewerUser, SecretPath + "/content");
        Output.WriteLine($"viewer: {denied}");

        denied.Should().NotContain(ServedAsReader,
            "the owner hub must refuse the collection lookup of a caller holding no Read on the node");
    }

    [Fact]
    public async Task ContentList_TheGrantHolder_IsServed_UnderTheSameSplit()
    {
        var answer = await ContentList(HolderUser, SecretPath + "/content");
        Output.WriteLine($"holder: {answer}");

        answer.Should().Contain(ServedAsReader,
            "the holder's grant covers the node — if this fails, the denial above proves nothing");
    }
}
