using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.ContentCollections;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A picture upload is authorized BEFORE anything is saved</b> (<see cref="NodeImageUpload"/>,
/// MeshWeaver#6575). The save runs on the calling hub straight into the collection's storage, where
/// no owner gate sees it; the owner's Update check used to arrive only with the node update that
/// FOLLOWS the save, so a caller holding Read alone wrote the file and the refusal then deleted it.
///
/// <para>"Nothing at any point" is measured on the managed <see cref="NodeImageUpload.Folder"/>:
/// the file-system save creates that directory and the clean-up deletes only the file, so the
/// directory's absence proves the save never ran — a check on the file alone would pass over the
/// old save-then-delete order too.</para>
///
/// <para>Each upload is BUILT as the user and SUBSCRIBED under a hub principal, so the decision is
/// proven to be the caller captured at entry, not the subscribing thread's identity. The editor is
/// the negative control: without it, the refusal proves nothing.</para>
/// </summary>
public class PictureUploadIsAuthorizedBeforeSaveTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PictureType = "PictureGateProbe";
    private const string Target = "PictureGateTarget";
    private const string ReaderUser = "picturegate-reader";
    private const string EditorUser = "picturegate-editor";

    private readonly string _contentRoot = Path.Combine(
        AppContext.BaseDirectory, "Files", "PictureGate", Guid.NewGuid().ToString("N"));

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter grants Public the Admin role in every
    // default partition, under which every caller could write and the refusal would be vacuous.
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_contentRoot);
        return ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(PictureType)
                {
                    Name = "Picture Gate Probe",
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
                new MeshNode(Target) { Name = "Picture Gate Target", NodeType = PictureType },
                AssignmentNodeFactory.UserRole(ReaderUser, "Viewer", Target),
                AssignmentNodeFactory.UserRole(EditorUser, "Editor", Target));
    }

    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7, 7, 7];

    private string PictureFolder => Path.Combine(_contentRoot, NodeImageUpload.Folder);

    /// <summary>Builds the upload as <paramref name="userId"/>, subscribes it as the hub.</summary>
    private IObservable<System.Reactive.Notification<string>> UploadBuiltAsSubscribedAsHub(string userId)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetContext(new AccessContext { ObjectId = userId, Name = userId });
        var bytes = Png();
        var upload = NodeImageUpload.Replace(Mesh, Target, "me.png", bytes.Length, () => new MemoryStream(bytes));
        return Observable.Create<System.Reactive.Notification<string>>(observer =>
        {
            using (access.ImpersonateAsHub(Mesh))
                return upload.Materialize().Subscribe(observer);
        });
    }

    [Fact(Timeout = 120_000)]
    public async Task AReader_IsRefused_AndNothingIsStoredAtAnyPoint()
    {
        var outcome = await UploadBuiltAsSubscribedAsHub(ReaderUser)
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != System.Reactive.NotificationKind.OnCompleted,
                "the reader's upload must answer", TestContext.Current.CancellationToken);

        outcome.Kind.Should().Be(System.Reactive.NotificationKind.OnError,
            "Read alone does not entitle a caller to change the node's picture");
        Directory.Exists(PictureFolder).Should().BeFalse(
            "the refusal comes BEFORE the save: the save would have created the picture folder, "
            + "and the old save-then-delete order left it behind");
        outcome.Exception.Should().BeOfType<NodeImageUploadException>()
            .Which.Reason.Should().Be(NodeImageUploadFailure.NotPermitted);
        var node = await Mesh.GetMeshNodeStream(Target).Should().Within(TestTimeouts.Convergence)
            .Emit("the node is readable");
        node.Icon.Should().BeNull("a refused upload never points the icon anywhere");
    }

    [Fact(Timeout = 120_000)]
    public async Task AnEditor_Uploads_AndThePictureIsStored()
    {
        var outcome = await UploadBuiltAsSubscribedAsHub(EditorUser)
            .Should().Within(TestTimeouts.Convergence)
            .Match(n => n.Kind != System.Reactive.NotificationKind.OnCompleted,
                "the editor's upload must answer", TestContext.Current.CancellationToken);

        outcome.Kind.Should().Be(System.Reactive.NotificationKind.OnNext,
            $"the editor holds Update on the node — if this fails, the refusal above proves nothing ({outcome.Exception})");
        var icon = outcome.Value;
        File.Exists(Path.Combine(_contentRoot, NodeImageUpload.ManagedFilePath(icon)!)).Should().BeTrue();
        await Mesh.GetMeshNodeStream(Target).Where(n => n.Icon == icon)
            .Should().Within(TestTimeouts.Convergence).Emit("the Icon points at the new picture");
    }
}
