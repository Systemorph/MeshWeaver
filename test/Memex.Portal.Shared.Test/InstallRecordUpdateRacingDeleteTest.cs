using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>An install-record update racing the record's delete must never leave the record behind.</b>
///
/// <para>The boot repair pass's install-record migration (<see cref="PackageAutoUpdateMigration"/>)
/// updates every reminder-only record through <c>GetMeshNodeStream(record).Update</c>; a partition
/// delete (<c>InstallRecordPartitionTeardownHandler</c>) or an uninstall removes the same record.
/// When the update's patch committed at the record's owner after the delete had removed the row,
/// the owner's post-commit flush wrote it straight back — the delete had already answered
/// <c>removed=true</c>. Measured before the fix: 7 of 40 rounds of exactly this shape left the
/// record in storage; it is what reddened <c>InstallRecordFollowsItsPartitionTest</c> and
/// <c>PackageUninstallTest</c> in CI. The deterministic half of the guard is
/// <c>MeshWeaver.Graph.Test.UpdateNeverResurrectsADeletedNodeTest</c>.</para>
/// </summary>
public class InstallRecordUpdateRacingDeleteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    [Fact(Timeout = 240000)]
    public async Task TheMigrationsUpdateRacingTheRecordsDelete_NeverLeavesTheRecordBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var survivors = 0;
        for (var i = 0; i < 40; i++)
        {
            var id = "racepkg" + Guid.NewGuid().ToString("N")[..8];
            var path = $"{PackageInstaller.InstalledPartition}/{id}";
            await access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(MeshNode.FromPath(path) with
                {
                    NodeType = PackageInstaller.PackageNodeType,
                    Name = id,
                    State = MeshNodeState.Active,
                    Content = new PackageManifest { Id = id, Name = id, Version = "1.0.0", TargetPartition = id },
                }))
                .Timeout(TestTimeouts.Convergence).Await(ct);
            // Warm the owner, so the migration's patch meets a live per-node hub holding the record.
            await access.RunAsSystem(() => Mesh.GetMeshNodeStream(path))
                .Where(n => n is not null).FirstAsync()
                .Timeout(TestTimeouts.Convergence).Await(ct);

            var update = access.RunAsSystem(() => Mesh.GetMeshNodeStream(path)
                    .Update<PackageManifest>(PackageAutoUpdateMigration.Migrated))
                .Take(1)
                .Select(_ => "applied")
                .Catch((Exception ex) => Observable.Return($"refused: {ex.GetType().Name}"))
                .Replay();
            using var inFlight = update.Connect();

            var removed = await access.RunAsSystem(() => NodeFactory.DeleteNode(path))
                .Timeout(TestTimeouts.Convergence).Await(ct);
            var outcome = await update.Timeout(TestTimeouts.Convergence).Await(ct);
            var survived = await storage.Exists(path).FirstAsync()
                .Timeout(TestTimeouts.Convergence).Await(ct);
            Output.WriteLine($"round {i}: removed={removed} update={outcome} survived={survived}");
            if (survived)
                survivors++;
        }

        survivors.Should().Be(0,
            "a delete that answered success must leave nothing behind — the migration's update "
            + "committing at the record's owner after the delete must not write the record back");
    }
}
