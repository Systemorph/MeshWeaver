using System;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The declared-access step decides "pre-installed" from the partition's LIVE ROOT, never from a
/// stale manifest</b> — MeshWeaver#5297 / #5578.
///
/// <para><b>The live shape.</b> <c>Hosting</c> on memex-cloud stopped being pre-installed
/// (MeshWeaver.Plugins#1959). Its root node said so; its install record kept
/// <c>preInstalled: true</c> until a re-install three days later. The Store's gating reconcile reads
/// the root and gates the partition (a policy withholding public read, a Public/Anonymous deny on every
/// child); the boot repair pass hands this step the record, whose legacy heal read those very denies as
/// pre-#902 damage, retired them and wrote <c>publicRead: true</c> over the gate's policy. Neither
/// write stuck: <c>Hosting/_Policy</c> reached version 238, and the gate logged, truthfully, that its
/// shape was "NOT STAYING".</para>
/// </summary>
public class AStaleRecordDoesNotUndoTheGateTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private ILogger Logger => Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger<AStaleRecordDoesNotUndoTheGateTest>();

    /// <summary>The install record's stored manifest: stamped while the package WAS pre-installed.</summary>
    private static PackageManifest StaleRecord(string id) => new()
    {
        Id = id,
        Kind = PackageKind.NodeRepo,
        TargetPartition = id,
        PreInstalled = true,
    };

    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1)
            .DefaultIfEmpty(null)
            .Timeout(60.Seconds())
            .Await(TestContext.Current.CancellationToken);

    private Task WriteAsSystem(MeshNode node) =>
        Access.RunAsSystem(() => MeshService.CreateOrUpdateNode(node))
            .Should().Within(60.Seconds())
            .Emit($"writing the precondition node '{node.Path}' must land",
                cancellationToken: TestContext.Current.CancellationToken);

    private Task Establish(PackageManifest manifest, string partition) =>
        Access.RunAsSystem(() => PackageInstaller.EnsureDeclaredAccess(
                Mesh, manifest, partition, Logger))
            .Should().Within(120.Seconds())
            .Emit("the declared-access step must complete",
                cancellationToken: TestContext.Current.CancellationToken);

    private async Task<bool?> PolicyPublicRead(string partition)
    {
        var policy = await Read($"{partition}/_Policy");
        return policy?.ContentAs<PartitionAccessPolicy>(Mesh.JsonSerializerOptions)?.PublicRead;
    }

    private static string Deny(string scope, string subject) => $"{scope}/_Access/{subject}_Access";

    /// <summary>
    /// Arranges the gate's shape on a plugin partition whose root declares
    /// <paramref name="rootPreInstalled"/>: the policy the gate writes (public read withheld, the
    /// paywall redirect) and the Public/Anonymous deny pair on an ordinary child.
    /// </summary>
    private async Task Arrange(string partition, bool rootPreInstalled)
    {
        var content = new JsonObject { ["$type"] = "PluginContent" };
        if (rootPreInstalled)
            content["preInstalled"] = true;
        await WriteAsSystem(new MeshNode(partition)
        {
            NodeType = "Space",
            State = MeshNodeState.Active,
            Content = content,
        });
        await WriteAsSystem(new MeshNode("Guide", partition) { NodeType = "Markdown", State = MeshNodeState.Active });
        await WriteAsSystem(new MeshNode("_Policy", partition)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            State = MeshNodeState.Active,
            Content = new PartitionAccessPolicy { PublicRead = false, RedirectOnDenied = $"{partition}/Subscribe" },
        });
        await WriteAsSystem(PackageInstaller.ViewerAssignment(
            $"{partition}/Guide", WellKnownUsers.Public, denied: true));
        await WriteAsSystem(PackageInstaller.ViewerAssignment(
            $"{partition}/Guide", WellKnownUsers.Anonymous, denied: true));
    }

    /// <summary>
    /// The root says GATED, the record says pre-installed: the step must leave the gate's shape alone.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARootThatIsNoLongerPreInstalled_KeepsTheGatesDeniesAndPolicy()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        const string partition = "DemotedPkg";
        await Arrange(partition, rootPreInstalled: false);

        await Establish(StaleRecord(partition), partition);

        (await Read(Deny($"{partition}/Guide", WellKnownUsers.Public))).Should().NotBeNull(
            "THE assertion: the root says this package is gated, so the Public deny the gate wrote on "
            + "its child is the live access model, not pre-#902 damage — retiring it is the write the "
            + "gate's next pass finds missing and reports as NOT STAYING (MeshWeaver#5297)");
        (await Read(Deny($"{partition}/Guide", WellKnownUsers.Anonymous))).Should().NotBeNull(
            "both halves of the pair");
        (await PolicyPublicRead(partition)).Should().BeFalse(
            "and the policy keeps withholding public read — a PublicRead policy here publishes every "
            + "gated page on the C# read path and is the other half of the ping-pong");
    }

    /// <summary>
    /// The control on the other side: a root that really IS pre-installed is still healed from the
    /// legacy gate, so the fix is "read the root", not "stop healing".
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARootThatIsPreInstalled_IsStillHealed()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        const string partition = "BaselinePkg";
        await Arrange(partition, rootPreInstalled: true);

        await Establish(StaleRecord(partition), partition);

        (await Read(Deny($"{partition}/Guide", WellKnownUsers.Public))).Should().BeNull(
            "a pre-installed root with a gated child is the pre-#902 fingerprint the heal exists for, "
            + "and it must still be retired");
        (await PolicyPublicRead(partition)).Should().BeTrue(
            "and the pre-installed partition is published");
    }

    /// <summary>The pure reader: which roots carry the declaration, and what an absent flag means.</summary>
    [Fact]
    public void PreInstalledOnRoot_ReadsOnlyAPluginRoot_AndAnAbsentFlagIsFalse()
    {
        var options = Mesh.JsonSerializerOptions;
        PackageInstaller.PreInstalledOnRoot(null, options).Should().BeNull("no root, no statement");
        PackageInstaller.PreInstalledOnRoot(new MeshNode("X") { NodeType = "Space" }, options)
            .Should().BeNull("a root without content says nothing");
        PackageInstaller.PreInstalledOnRoot(
                new MeshNode("X") { Content = new JsonObject { ["$type"] = "Space" } }, options)
            .Should().BeNull("a non-plugin root leaves the manifest to decide");
        PackageInstaller.PreInstalledOnRoot(
                new MeshNode("X") { Content = new JsonObject { ["$type"] = "PluginContent" } }, options)
            .Should().BeFalse("the serializer omits a false bool, so a stored gated root has no flag at all");
        PackageInstaller.PreInstalledOnRoot(
                new MeshNode("X")
                {
                    Content = new JsonObject { ["$type"] = "PluginContent", ["preInstalled"] = true },
                }, options)
            .Should().BeTrue();
    }
}
