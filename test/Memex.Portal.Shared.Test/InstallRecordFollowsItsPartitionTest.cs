using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>An install record must not outlive the partition it points at</b>
/// (Systemorph/MeshWeaver#3451, the second half).
///
/// <para>A package's install record lives at <c>Plugins/{packageId}</c> — in the RECORDS partition,
/// never in the package's own — so deleting the installed space left the record behind, aimed at a
/// partition that no longer exists. Nothing reconciled it, and
/// <c>InstalledPackageRepairService</c> re-drove <c>PackageInstaller.EnsureDeclaredAccess</c> at that
/// dead partition on EVERY boot: a permanent per-boot error for every partition anyone had ever
/// deleted (21 of them on one portal when this was filed), and the writer that re-armed the
/// resurrection race on every restart.</para>
///
/// <para><b>The fix is referential integrity maintained by the side that owns the reference.</b> Core
/// deliberately knows nothing about <c>Plugins/Package</c> — teaching the delete pipeline about it
/// would re-introduce exactly the coupling #3436 removed — so the plugin catalog registers its OWN
/// <c>INodePostDeletionHandler</c>, matching the same structural partition-root predicate the
/// platform's partition teardown uses.</para>
/// </summary>
public class InstallRecordFollowsItsPartitionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The <c>Store/Plugin</c> stand-in: a partition-root NodeType that declares no
    /// <c>OwnsPartition</c>, because what provisioned its partition was the installer.</summary>
    private const string PluginLikeNodeType = "TestStore/InstalledPlugin";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .AddMeshNodes(new MeshNode(PluginLikeNodeType)
            {
                Name = "Installed plugin root",
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition { DefaultNamespace = "", RestrictedToNamespaces = [""] },
            });

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private IStorageAdapter Persistence => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private static string NewPackageId() => "recpkg" + Guid.NewGuid().ToString("N")[..8];

    private static string RecordPath(string packageId) =>
        $"{PackageInstaller.InstalledPartition}/{packageId}";

    /// <summary>
    /// Writes what an install leaves behind: the record in the <c>Plugins</c> partition, the
    /// partition root, and one child so the delete has a real subtree to drain.
    /// </summary>
    private async Task<string> Install()
    {
        var packageId = NewPackageId();
        var manifest = new PackageManifest
        {
            Id = packageId,
            Name = $"Package {packageId}",
            Version = "1.0.0",
            TargetPartition = packageId,
        };

        await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(
                MeshNode.FromPath(RecordPath(packageId)) with
                {
                    NodeType = PackageInstaller.PackageNodeType,
                    Name = manifest.Name,
                    State = MeshNodeState.Active,
                    Content = manifest,
                }))
            .Timeout(TestTimeouts.Convergence).Await();

        await CreateAsSystem(new MeshNode(packageId)
        {
            Name = manifest.Name,
            NodeType = PluginLikeNodeType,
            State = MeshNodeState.Active,
        });
        await CreateAsSystem(new MeshNode("Page", packageId)
        {
            Name = "Page",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = "# page" },
        });

        (await Exists(RecordPath(packageId))).Should().BeTrue(
            "the fixture must actually record the install, or the assertions below prove nothing");
        Output.WriteLine($"installed '{packageId}' with record {RecordPath(packageId)}");
        return packageId;
    }

    private async Task<CreateNodeResponse> CreateAsSystem(MeshNode node)
    {
        var response = await Access
            .RunAsSystem(() => ObserveNodeOperation(new CreateNodeRequest(node)))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(TestTimeouts.Convergence)
            .Await();
        response.Success.Should().BeTrue($"the fixture write '{node.Path}' must land: {response.Error}");
        return response;
    }

    private async Task DeleteAsSystem(string path)
    {
        var response = await Access
            .RunAsSystem(() => ObserveNodeOperation(
                new DeleteNodeRequest(path)
                {
                    Recursive = true,
                    IncludeSatellites = true,
                    ConfirmWarnings = true,
                    DeletedBy = WellKnownUsers.System,
                }))
            .FirstAsync()
            .Select(d => d.Message)
            .Timeout(TestTimeouts.CrossSilo)
            .Await();
        Output.WriteLine($"delete {path} success={response.Success} error={response.Error}");
        response.Success.Should().BeTrue($"the delete itself must succeed: {response.Error}");
    }

    private async Task<bool> Exists(string path) =>
        await Persistence.Exists(path).FirstAsync().Timeout(TestTimeouts.Convergence).Await();

    /// <summary>
    /// 🚨 <b>THE FALSIFIER.</b> On the pre-fix code the delete succeeds, the partition's nodes are
    /// gone, and <c>Plugins/{packageId}</c> is still there — pointing at nothing, and re-asserting
    /// <c>{packageId}/_Policy</c> on every boot for the rest of the instance's life.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task DeletingAnInstalledPartition_RemovesItsInstallRecord()
    {
        var packageId = await Install();

        await DeleteAsSystem(packageId);

        (await Exists(RecordPath(packageId))).Should().BeFalse(
            $"'{RecordPath(packageId)}' names a partition that no longer exists. A record that "
            + "outlives its partition is a dangling reference the boot repair pass keeps writing "
            + "into forever — a permanent per-boot error for every partition anyone ever deleted, "
            + "and the writer that re-armed the resurrection race on every restart (#3451)");
    }

    /// <summary>
    /// The positive control that keeps the assertion above honest: a NESTED node is not a partition
    /// root, and deleting one must never remove the install record. A handler that fired on every
    /// delete would pass the test above while uninstalling a package on its first page deletion.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task DeletingANestedNode_LeavesTheInstallRecordAlone()
    {
        var packageId = await Install();

        await DeleteAsSystem($"{packageId}/Page");

        (await Exists(RecordPath(packageId))).Should().BeTrue(
            "only a partition ROOT ends an install — deleting a page out of an installed package "
            + "leaves the package installed");
    }

    /// <summary>
    /// The second control: the handler must remove the record for the partition that was deleted and
    /// no other. A handler that removed every record on any partition delete would pass both tests
    /// above.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task DeletingADifferentPartition_LeavesOtherInstallRecordsAlone()
    {
        var kept = await Install();
        var deleted = await Install();

        await DeleteAsSystem(deleted);

        (await Exists(RecordPath(deleted))).Should().BeFalse("its partition is gone");
        (await Exists(RecordPath(kept))).Should().BeTrue(
            $"'{kept}' was not touched — the teardown must be scoped to the partition that was "
            + "actually deleted");
    }

    /// <summary>
    /// The registration itself, asserted on the mesh this repository ships and stated WITHOUT naming
    /// the new type — so this file compiles, and fails, against the pre-fix source too. Two
    /// structural teardowns must reach an arbitrary partition root (the platform's backing-store
    /// drop and the catalog's install-record removal), and only ONE of them may reach the records
    /// partition itself.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void TwoStructuralTeardownsCoverAnArbitraryPartitionRoot()
    {
        var handlers = Mesh.ServiceProvider.GetServices<INodePostDeletionHandler>().ToList();
        var probe = new MeshNode("ProbePartition")
        {
            NodeType = "TestProbe/UnknownInMeshType",
            State = MeshNodeState.Active,
        };
        var recordsPartition = new MeshNode(PackageInstaller.InstalledPartition)
        {
            NodeType = "Space",
            State = MeshNodeState.Active,
        };

        handlers.Count(h => h.Matches(probe)).Should().Be(2,
            "a partition root fires TWO structural teardowns — the platform's backing-store drop and "
            + "the catalog's install-record removal (#3451) — and a NodeType neither of them could "
            + "have enumerated must reach both");
        handlers.Count(h => h.Matches(recordsPartition)).Should().Be(1,
            "the RECORDS partition is excluded from the install-record teardown: its records go with "
            + "it, and cascading each one first would race the drain that just removed them — only "
            + "the platform's backing-store drop applies there");
    }
}
