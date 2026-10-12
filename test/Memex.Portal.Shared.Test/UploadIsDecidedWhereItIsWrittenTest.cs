#pragma warning disable CS1591

using System.IO;
using System.Reactive.Linq;
using System.Threading;
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
/// 🚨 <b>#6585 — an upload is authorized WHERE ITS BYTES ARE COMMITTED.</b> The bytes travel to the
/// node that owns the collection (<see cref="SaveContentFileRequest"/>), and that node's inbound gate
/// decides Update for the caller on the delivery that carries them. A caller-side pre-flight is only
/// an early answer.
///
/// <para>Before: <see cref="MeshOperations.Upload"/> asked Update on the CALLING hub, then wrote the
/// bytes through the calling hub's content service — so a grant revoked after that check did not
/// stop the write, and the write path itself carried no authorization at all.</para>
///
/// <para><b>The revocation is forced deterministically</b> through the framework's evaluator seam
/// (<see cref="MessageHubPermissionExtensions.WithPermissionEvaluator"/>), narrowed to one identity:
/// every check asked on a hub OTHER than the owner (the pre-flight) grants and, in the same breath,
/// revokes Update at the owner. So the pre-flight passes and the owner then sees only Read — the
/// time-of-check/time-of-use window, with no timing involved. Every other identity keeps the real
/// evaluator.</para>
/// </summary>
public class UploadIsDecidedWhereItIsWrittenTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string UploadType = "UploadOwnerGateProbe";
    private const string Target = "UploadOwnerGateTarget";
    private const string RevokedUser = "uploadowner-revoked";
    private const string ReaderUser = "uploadowner-reader";
    private const string EditorUser = "uploadowner-editor";

    /// <summary>1 once the revocable user's pre-flight has been answered: the owner then grants Read only.</summary>
    private int revoked;

    private readonly string _contentRoot = Path.Combine(
        AppContext.BaseDirectory, "Files", "UploadOwnerGate", Guid.NewGuid().ToString("N"));

    /// <summary>A collection registered on the MESH hub and exposed to children — the store a node
    /// hub's content service falls back to for a name the node does not declare itself.</summary>
    private const string HostCollection = "hostshared";

    private readonly string _hostRoot = Path.Combine(
        AppContext.BaseDirectory, "Files", "UploadOwnerGateHost", Guid.NewGuid().ToString("N"));

    private static TimeSpan Budget => TestTimeouts.Convergence;

    // 🚨 ConfigureMeshBase, not base.ConfigureMesh: the latter grants Public the Admin role in every
    // default partition, under which every caller could write and the refusals would be vacuous.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(_contentRoot);
        Directory.CreateDirectory(_hostRoot);
        return ConfigureMeshBase(builder)
            .ConfigureHub(hub => hub.AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = HostCollection,
                SourceType = "FileSystem",
                BasePath = _hostRoot,
                IsEditable = true,
                ExposeInChildren = true,
            }))
            .AddMeshNodes(
                new MeshNode(UploadType)
                {
                    Name = "Upload Owner Gate Probe",
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
                new MeshNode(Target) { Name = "Upload Owner Gate Target", NodeType = UploadType },
                AssignmentNodeFactory.UserRole(ReaderUser, "Viewer", Target),
                AssignmentNodeFactory.UserRole(EditorUser, "Editor", Target))
            // The mesh hub's delegate is what session hubs copy (SessionHubFactory); the node hubs
            // carry their own. Both WRAP the evaluator RLS installed — applied after it, and
            // WithPermissionEvaluator is a Set — so only the one identity's answer changes.
            .ConfigureHub(Wrap)
            .ConfigureDefaultNodeHub(Wrap);
    }

    private MessageHubConfiguration Wrap(MessageHubConfiguration config)
    {
        var configured = config.Get<EffectivePermissionsDelegate>()
            ?? throw new InvalidOperationException(
                "Row-level security installed no evaluator on this hub, so the rig would decide every "
                + "check rather than the one identity's — the test would pass for a reason it is not about.");
        return config.WithPermissionEvaluator((hub, path, userId) => Evaluate(configured, hub, path, userId));
    }

    private IObservable<Permission> Evaluate(
        EffectivePermissionsDelegate configured, IMessageHub hub, string nodePath, string userId)
    {
        if (userId != RevokedUser)
            return configured(hub, nodePath, userId);
        if (!string.Equals(hub.Address.Path, Target, StringComparison.OrdinalIgnoreCase))
        {
            // The caller-side check: it passes, and the grant is revoked the moment it has.
            Interlocked.Exchange(ref revoked, 1);
            return Observable.Return(Permission.All);
        }
        return Observable.Return(Volatile.Read(ref revoked) == 1 ? Permission.Read : Permission.All);
    }

    private IMessageHub SessionHub() => SessionHubFactory.Resolve(
        Mesh, "mcp", "uploadownergate",
        Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(UploadIsDecidedWhereItIsWrittenTest)));

    private Task<string> UploadAs(string userId, string path, byte[] bytes)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetContext(new AccessContext { ObjectId = userId, Name = userId });
        var upload = new MeshOperations(SessionHub()).Upload(path, bytes);
        using (access.ImpersonateAsHub(Mesh))
            return upload.Timeout(Budget).Await(TestContext.Current.CancellationToken);
    }

    private string Stored(string fileName) => Path.Combine(_contentRoot, fileName);

    [Fact]
    public async Task AGrantRevokedAfterThePreflight_IsRefusedByTheOwner_AndNothingIsStored()
    {
        var answer = await UploadAs(RevokedUser, $"{Target}/content/revoked.txt",
            System.Text.Encoding.UTF8.GetBytes("after the revocation"));
        Output.WriteLine($"revoked: {answer}");

        Volatile.Read(ref revoked).Should().Be(1, "the pre-flight ran and passed — this is the window under test");
        answer.Should().StartWith("Error:", "the owner decided Update on the delivery that carried the bytes")
            .And.Contain("Nothing was written");
        File.Exists(Stored("revoked.txt")).Should().BeFalse(
            "the bytes are committed only behind the owner's gate, so a revoked grant stores nothing");
    }

    [Fact]
    public async Task AReader_WritingStraightToTheOwner_IsRefused_AndNothingIsStored()
    {
        // No pre-flight on this path at all — the owner's gate is the only thing between a caller
        // and the store, which is the property #6585 asks for.
        var reader = new AccessContext { ObjectId = ReaderUser, Name = ReaderUser };
        var failure = await Record.ExceptionAsync(() => SessionHub()
            .SaveContentFile(Target)
            .To(ContentCollectionsExtensions.DefaultCollectionName, "direct.txt")
            .WithAccessContext(reader)
            .Post(System.Text.Encoding.UTF8.GetBytes("from a reader"))
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken));
        Output.WriteLine($"reader direct: {failure}");

        failure.Should().BeOfType<DeliveryFailureException>("the owner's gate refuses the delivery");
        ((DeliveryFailureException)failure!).Failure.ErrorType.Should().Be(ErrorType.Unauthorized);
        File.Exists(Stored("direct.txt")).Should().BeFalse("a refused write stores nothing");
    }

    [Fact]
    public async Task AnEditor_WritingStraightToTheOwner_IsStored()
    {
        var editor = new AccessContext { ObjectId = EditorUser, Name = EditorUser };
        var answer = await SessionHub()
            .SaveContentFile(Target)
            .To(ContentCollectionsExtensions.DefaultCollectionName, "nested/editor.txt")
            .WithAccessContext(editor)
            .Post(System.Text.Encoding.UTF8.GetBytes("from an editor"))
            .Timeout(Budget)
            .Await(TestContext.Current.CancellationToken);

        answer.Success.Should().BeTrue($"the editor holds Update on the node — if this fails the refusals above prove nothing ({answer.Error})");
        (await File.ReadAllTextAsync(Path.Combine(_contentRoot, "nested", "editor.txt"), TestContext.Current.CancellationToken))
            .Should().Be("from an editor");
    }

    [Fact]
    public async Task AnUploadLargerThanOneDelivery_TravelsStaged_IsStored_AndLeavesNoStagedBlob()
    {
        // Over the per-delivery budget, so the bytes go through the collection's staging folder
        // and the delivery carries only the handle — the owner still writes the file.
        var bytes = new byte[ContentDeliveryBudget.BudgetBytes + 64 * 1024];
        new Random(6585).NextBytes(bytes);

        var answer = await UploadAs(EditorUser, $"{Target}/content/large.bin", bytes);
        Output.WriteLine($"large: {answer}");

        answer.Should().Contain("\"Uploaded\"");
        (await File.ReadAllBytesAsync(Stored("large.bin"), TestContext.Current.CancellationToken))
            .Should().Equal(bytes);
        var staging = Path.Combine(_contentRoot, ContentStaging.Folder);
        (Directory.Exists(staging) ? Directory.GetFiles(staging) : [])
            .Should().BeEmpty("the staged blob is reclaimed when the write answers");
    }

    [Fact]
    public async Task AnEditorOfOneNode_CannotWriteIntoACollectionItInheritsFromItsHost()
    {
        // The node does not declare HostCollection; its content service resolves the name off the
        // mesh hub, whose store is shared by every node it hosts. Update on the node is no authority
        // over that store, so the owner-side write refuses it and nothing lands in the host's root.
        var answer = await UploadAs(EditorUser, $"{Target}/{HostCollection}/planted.txt",
            System.Text.Encoding.UTF8.GetBytes("into the host's store"));
        Output.WriteLine($"inherited: {answer}");

        answer.Should().StartWith("Error:").And.Contain("not declared by this node");
        File.Exists(Path.Combine(_hostRoot, "planted.txt")).Should().BeFalse(
            "a collection the node only inherits is not the node's to write into");
        File.Exists(Path.Combine(_hostRoot, Target, "planted.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task AnOversizedUpload_IntoAnInheritedCollection_StagesNothingInTheHostStore()
    {
        // Over budget, so the bytes would travel staged — and staging writes on the CALLING hub,
        // where no owner gate sees it. The inherited collection must be refused before staging, or
        // the host's staging folder holds an editor-of-one-node's blob until the age sweep (and for
        // good when the post is cancelled after staging).
        var bytes = new byte[ContentDeliveryBudget.BudgetBytes + 64 * 1024];
        new Random(65930).NextBytes(bytes);

        var answer = await UploadAs(EditorUser, $"{Target}/{HostCollection}/planted-large.bin", bytes);
        Output.WriteLine($"inherited, staged: {answer}");

        answer.Should().StartWith("Error:").And.Contain("not declared by this node");
        Directory.EnumerateFileSystemEntries(_hostRoot, "*", SearchOption.AllDirectories)
            .Should().BeEmpty("nothing — neither the file nor a staged blob — lands in the host's store");
    }

    [Fact]
    public async Task AStagedUpload_NeverReclaimsABlobAnotherTransferOfTheSameBytesStaged()
    {
        // Deterministic form of two concurrent uploads of identical bytes: the OTHER transfer's blob
        // is already in staging, fresh, under the bytes' content hash — exactly where it sits while
        // that transfer's owner is still probing or reading it. Content-addressed staging would adopt
        // that blob and reclaim it when THIS upload answers, deleting it under the other's read.
        var bytes = new byte[ContentDeliveryBudget.BudgetBytes + 64 * 1024];
        new Random(65931).NextBytes(bytes);
        var staging = Path.Combine(_contentRoot, ContentStaging.Folder);
        Directory.CreateDirectory(staging);
        var othersBlob = Path.Combine(staging, ContentStaging.ComputeHandle(bytes));
        await File.WriteAllBytesAsync(othersBlob, bytes, TestContext.Current.CancellationToken);

        var answer = await UploadAs(EditorUser, $"{Target}/content/twin.bin", bytes);
        Output.WriteLine($"twin: {answer}");

        answer.Should().Contain("\"Uploaded\"");
        (await File.ReadAllBytesAsync(Stored("twin.bin"), TestContext.Current.CancellationToken)).Should().Equal(bytes);
        File.Exists(othersBlob).Should().BeTrue("a transfer reclaims only the blob it staged itself");
        Directory.GetFiles(staging).Should().Equal([othersBlob], "and its own blob is reclaimed when the write answers");
    }

    [Fact]
    public async Task AStagedUpload_SweepsAStaleBlobADeadProducerLeft()
    {
        // A blob abandoned by an upload that never terminated (an HTTP cancellation after staging):
        // older than StaleAfter, so the next staged upload into the collection reclaims it first.
        var staging = Path.Combine(_contentRoot, ContentStaging.Folder);
        Directory.CreateDirectory(staging);
        var abandoned = Path.Combine(staging, new string('a', 64));
        await File.WriteAllBytesAsync(abandoned, [1, 2, 3], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - ContentStaging.StaleAfter - TimeSpan.FromHours(1));

        var bytes = new byte[ContentDeliveryBudget.BudgetBytes + 64 * 1024];
        new Random(6593).NextBytes(bytes);
        var answer = await UploadAs(EditorUser, $"{Target}/content/after-abandoned.bin", bytes);
        Output.WriteLine($"sweep: {answer}");

        answer.Should().Contain("\"Uploaded\"");
        File.Exists(abandoned).Should().BeFalse("a staged upload reclaims a dead producer's residue before staging");
        Directory.GetFiles(staging).Should().BeEmpty("and its own blob is reclaimed when the write answers");
    }
}
