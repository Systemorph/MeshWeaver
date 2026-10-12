#pragma warning disable CS1591

using System.Reactive.Linq;
using MeshWeaver.AI;   // MeshOperations - its namespace is a frozen binary contract (#2370)
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
/// UPLOAD IS A WRITE (Systemorph/MeshWeaver#6575). <c>MeshOperations.Upload</c> - the one verb behind the
/// MCP <c>upload</c> tool, <c>POST /api/mesh/upload</c>, the CLI and <c>Import</c>'s per-file re-upload -
/// read the owner's collection configuration (a Read) and then saved the bytes through the CALLING hub's
/// content service straight against the collection's storage, where no owner-side delivery gate sees a
/// write. A caller holding Read alone could therefore add or overwrite files in a node's editable content
/// collection. The verb now authorizes itself: Read on the node that owns the collection (a caller who may
/// not read it is answered exactly as an absent path is), then Update on it, through the pre-flight
/// <c>Recycle</c> and <c>Patch</c> already take.
///
/// <para>The fixture is the one <see cref="SessionDenialIsAnAnswerTest"/> pins the pre-flight with:
/// <c>ConfigureMeshBase</c> (never <c>base.ConfigureMesh</c>, which grants Public the Admin role and would
/// make every assertion here vacuous), role assignments scoped to one partition, and a REAL session hub
/// from <see cref="SessionHubFactory"/> - the surface MCP and REST issue on - over a node whose own
/// <c>content</c> collection is editable and file-system backed.</para>
///
/// <para>Negative control: against the unfixed verb the Viewer's upload answers <c>Uploaded</c> and the file
/// lands on disk, so both the answer assertion and the "file is absent" assertion of the Viewer test fail.</para>
/// </summary>
public class UploadRequiresUpdateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "UploadGate";
    private const string ProbeType = "UploadGateProbe";
    private const string NodePath = Partition + "/Doc";

    /// <summary>Read|Execute|Api - no Update anywhere in the partition.</summary>
    private const string ViewerUser = "upload-viewer";

    /// <summary>Holds Update in the partition.</summary>
    private const string EditorUser = "upload-editor";

    /// <summary>No assignment at all - may not even read the node.</summary>
    private const string StrangerUser = "upload-stranger";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private readonly string contentRoot = Path.Combine(
        AppContext.BaseDirectory, "Files", "UploadGate", Guid.NewGuid().ToString("N"));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(contentRoot);
        return ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(ProbeType)
                {
                    Name = "Upload Gate Probe",
                    // An EDITABLE default collection - the shape a user partition's `content` collection has.
                    HubConfiguration = config => config
                        .AddContentCollection(_ => new ContentCollectionConfig
                        {
                            Name = ContentCollectionsExtensions.DefaultCollectionName,
                            SourceType = "FileSystem",
                            BasePath = contentRoot,
                            IsEditable = true,
                            ExposeInChildren = true,
                        }),
                },
                new MeshNode(Partition) { Name = "Upload Gate", NodeType = "Markdown" },
                new MeshNode("Doc", Partition) { Name = "Doc", NodeType = ProbeType },
                AssignmentNodeFactory.UserRole(ViewerUser, "Viewer", Partition),
                AssignmentNodeFactory.UserRole(EditorUser, "Editor", Partition));
    }

    private IMessageHub SessionHub() => SessionHubFactory.Resolve(
        Mesh,
        "mcp",
        "upload",
        Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(UploadRequiresUpdateTest)));

    private void ActAs(string userId) => Mesh.ServiceProvider.GetRequiredService<AccessService>()
        .SetContext(new AccessContext { ObjectId = userId, Name = userId });

    private async Task<string> Upload(string path, string text = "hello")
    {
        var answer = await new MeshOperations(SessionHub())
            .Upload(path, System.Text.Encoding.UTF8.GetBytes(text))
            .FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"Upload({path}) -> {answer}");
        return answer;
    }

    private string FileOnDisk(string name) => Path.Combine(contentRoot, name);

    private async Task<PermissionCheckOutcome> Mesh_Check(string userId, Permission permission)
        => await Mesh.CheckPermissionOutcome(NodePath, userId, permission)
            .Take(1).Timeout(Budget).Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// THE DEFECT: a Viewer (Read, no Update) uploading into the node's editable collection. Must be
    /// refused, name the missing permission, and write NOTHING.
    /// </summary>
    [Fact]
    public async Task AReaderWithoutUpdateIsRefusedAndNothingIsWritten()
    {
        (await Mesh_Check(ViewerUser, Permission.Read)).IsGranted.Should().BeTrue(
            "the Viewer reads the node - if this flips, the refusal below would be the 'cannot read' one");
        (await Mesh_Check(ViewerUser, Permission.Update)).IsGranted.Should().BeFalse(
            "a Viewer holds Read|Execute|Api and no Update - if this flips, the test proves nothing");

        ActAs(ViewerUser);
        var answer = await Upload($"{NodePath}/content/viewer.txt");

        answer.Should().StartWith("Error:").And.Contain("Update permission",
            "a reader without Update is told which permission is missing");
        answer.Should().NotContain("Uploaded");
        File.Exists(FileOnDisk("viewer.txt")).Should().BeFalse(
            "a refused upload writes nothing - the unfixed verb saved the file with Read alone");
    }

    /// <summary>
    /// The case that could have FALSIFIED the one above: a gate that refused everybody would pass it and
    /// be a worse bug than the hole it closes. An Editor holds Update and must still upload.
    /// </summary>
    [Fact]
    public async Task AnEditorStillUploads()
    {
        (await Mesh_Check(EditorUser, Permission.Update)).IsGranted.Should().BeTrue(
            "Role.Editor carries Update - if this flips, the gate is refusing the role it exists for");

        ActAs(EditorUser);
        var answer = await Upload($"{NodePath}/content/editor.txt", "from the editor");

        answer.Should().Contain("Uploaded");
        File.Exists(FileOnDisk("editor.txt")).Should().BeTrue("an authorized upload lands");
        (await File.ReadAllTextAsync(FileOnDisk("editor.txt"), TestContext.Current.CancellationToken))
            .Should().Be("from the editor");
    }

    /// <summary>
    /// A caller who may not even READ the node is answered exactly as an absent path is - the answer must
    /// not disclose that the node exists, nor that it has an editable collection.
    /// </summary>
    [Fact]
    public async Task ACallerWhoCannotReadTheNodeGetsTheAnswerAnAbsentPathGets()
    {
        (await Mesh_Check(StrangerUser, Permission.Read)).IsGranted.Should().BeFalse(
            "the stranger has no assignment - if this grants, the case below is vacuous");

        ActAs(StrangerUser);
        var strangerPath = $"{NodePath}/content/stranger.txt";
        var absentPath = "UploadGateNoSuchPartition/Doc/content/stranger.txt";

        var denied = await Upload(strangerPath);
        var absent = await Upload(absentPath);

        absent.Should().Be($"Error: no matching node for path '{absentPath}'");
        denied.Should().Be($"Error: no matching node for path '{strangerPath}'",
            "a caller who may not read the node gets the answer an absent path gets");
        File.Exists(FileOnDisk("stranger.txt")).Should().BeFalse();
    }

    /// <summary>
    /// An unfinished check is not a denial and not a grant: it refuses (nothing written), says it did not
    /// complete, and does not accuse the caller of lacking a permission. Asserted on the pure answer
    /// because an undetermined fold is not reproducible fast and deterministically in a single-silo mesh.
    /// </summary>
    [Fact]
    public void AnUnfinishedCheckRefusesAndSaysSoWithoutAccusing()
    {
        var answer = MeshOperations.UploadRefusal(
            PermissionCheckOutcome.Undetermined("the fold stalled"), "p/content/f.txt", "p");

        answer.Should().NotBeNull();
        answer!.Should().StartWith("Error:")
            .And.Contain("did not complete")
            .And.Contain("Nothing was written")
            .And.NotContain("access denied");
    }

    /// <summary>A definitive denial names the missing permission and the node that owns the collection.</summary>
    [Fact]
    public void ADefiniteDenialNamesTheMissingPermission()
    {
        var answer = MeshOperations.UploadRefusal(PermissionCheckOutcome.Denied, "p/content/f.txt", "p");

        answer.Should().NotBeNull();
        answer!.Should().StartWith("Error:")
            .And.Contain("Update permission")
            .And.Contain("'p'")
            .And.Contain("Nothing was written");
    }
}
