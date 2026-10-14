#pragma warning disable CS1591

using System.IO;
using System.Reactive.Linq;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.ContentCollections;
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
/// 🚨 <b>An upload is a WRITE and is authorized as one</b>: <see cref="MeshOperations.Upload"/>
/// (the MCP <c>upload</c> tool and <c>POST /api/mesh/upload</c>) requires Update on the node that
/// owns the collection, for the caller captured at its entry.
///
/// <para>Before the fix the only gate on the way was the collection-config lookup, which the
/// owner serves to any READER; the save itself runs on the calling hub's content service straight
/// into the collection's storage, where no owner gate sees it. So a caller holding Read alone wrote
/// files into a node's editable content collection.</para>
///
/// <para>Each upload is BUILT as the caller and SUBSCRIBED under a hub principal — the
/// continuation-on-another-identity shape of MeshWeaver#6570 — so the decision is proven to be the
/// caller's, not the thread's. The editor is the negative control: without it a refusal proves
/// nothing.</para>
/// </summary>
public class UploadRequiresUpdateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string UploadType = "UploadGateProbe";
    private const string Target = "UploadGateTarget";
    private const string ReaderUser = "uploadgate-reader";
    private const string EditorUser = "uploadgate-editor";
    private const string StrangerUser = "uploadgate-stranger";
    private const string AbsentRoot = "UploadGateNoSuchRoot";

    private readonly string _contentRoot = Path.Combine(
        AppContext.BaseDirectory, "Files", "UploadGate", Guid.NewGuid().ToString("N"));

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter grants Public the Admin role in every
    // default partition, under which every caller could write and the refusal would be vacuous.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_contentRoot);
        return ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(UploadType)
                {
                    Name = "Upload Gate Probe",
                    HubConfiguration = config => config
                        .AddContentCollection(_ => new ContentCollectionConfig
                        {
                            Name = ContentCollectionsExtensions.DefaultCollectionName,
                            SourceType = "FileSystem",
                            BasePath = _contentRoot,
                            IsEditable = true,
                            ExposeInChildren = true,
                        }),
                },
                new MeshNode(Target) { Name = "Upload Gate Target", NodeType = UploadType },
                AssignmentNodeFactory.UserRole(ReaderUser, "Viewer", Target),
                AssignmentNodeFactory.UserRole(EditorUser, "Editor", Target));
    }

    private IMessageHub SessionHub() => SessionHubFactory.Resolve(
        Mesh, "mcp", "uploadgate",
        Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(UploadRequiresUpdateTest)));

    private Task<string> UploadBuiltAsSubscribedAsHub(string userId, string path, string text)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetContext(new AccessContext { ObjectId = userId, Name = userId });
        var upload = new MeshOperations(SessionHub()).Upload(path, System.Text.Encoding.UTF8.GetBytes(text));
        using (access.ImpersonateAsHub(Mesh))
            return upload.Timeout(Budget).Await(TestContext.Current.CancellationToken);
    }

    private string Stored(string fileName) => Path.Combine(_contentRoot, fileName);

    [Fact]
    public async Task AReader_IsRefused_AndNothingIsStored()
    {
        var answer = await UploadBuiltAsSubscribedAsHub(ReaderUser, $"{Target}/content/reader.txt", "from a reader");
        Output.WriteLine($"reader: {answer}");

        answer.Should().StartWith("Error:", "Read alone does not entitle a caller to write into the node's content")
            .And.Contain("lacks Update permission", "a reader already knows the node exists, so the missing permission is named");
        File.Exists(Stored("reader.txt")).Should().BeFalse("a refused upload writes nothing");
    }

    [Fact]
    public async Task AnEditor_Uploads_AndTheFileIsStored()
    {
        var answer = await UploadBuiltAsSubscribedAsHub(EditorUser, $"{Target}/content/editor.txt", "from an editor");
        Output.WriteLine($"editor: {answer}");

        answer.Should().Contain("\"Uploaded\"",
            "the editor holds Update on the node — if this fails, the refusal above proves nothing");
        (await File.ReadAllTextAsync(Stored("editor.txt"), TestContext.Current.CancellationToken))
            .Should().Be("from an editor");
    }

    [Fact]
    public async Task ACallerWhoCannotRead_GetsTheAbsentAnswer_AndNothingIsStored()
    {
        const string deniedPath = Target + "/content/stranger.txt";
        const string absentPath = AbsentRoot + "/content/stranger.txt";
        var denied = await UploadBuiltAsSubscribedAsHub(StrangerUser, deniedPath, "from a stranger");
        var absent = await UploadBuiltAsSubscribedAsHub(StrangerUser, absentPath, "from a stranger");
        Output.WriteLine($"denied: {denied}");
        Output.WriteLine($"absent: {absent}");

        denied.Replace(deniedPath, "<path>").Should().Be(absent.Replace(absentPath, "<path>"),
            "a caller who may not read the node must not learn from the refusal that it exists");
        File.Exists(Stored("stranger.txt")).Should().BeFalse("a refused upload writes nothing");
    }
}
