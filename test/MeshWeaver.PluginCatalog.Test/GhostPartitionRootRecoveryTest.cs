#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// GHOST-ROOT RECOVERY (#902, origin #638). A top-level partition root that exists to the
/// uniqueness check but carries no content and no grants — the residue of a non-atomic top-level
/// create — must be REPAIRED by installing the package that owns it, not left permanently
/// unrecoverable.
///
/// <para>This is the state the production portal was in: <c>get @Agent</c> answered <i>Not found</i>,
/// <c>search namespace:Agent</c> returned zero, there was no version history to restore — and
/// <c>create</c> refused with <b>"Node already exists: Agent"</b>. Every ordinary route to bring the
/// catalog back was closed: the node could be neither read nor re-created, and no mechanism existed
/// that would rewrite it.</para>
///
/// <para>The ghost is planted the way one really appears: written STRAIGHT to storage, bypassing
/// every create path (the create path refuses a content-less node outright — which is precisely why
/// a ghost can only ever come from a half-completed write, and why nothing on the create path can
/// clear it).</para>
/// </summary>
public class GhostPartitionRootRecoveryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A signed-in principal holding NOTHING — no role, no grant, anywhere.</summary>
    private const string PlainUser = "plain-user";

    /// <summary>Same DB-served declaration the deployments make; see PreInstalledPackageInstallTest.</summary>
    private static readonly IReadOnlySet<string> DbServed =
        new HashSet<string>(StringComparer.Ordinal) { "Agent" };

    // No IPackageSource in DI on purpose: the startup default install must be a no-op here, so the
    // test body alone decides when the install runs — after the ghost is planted.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddAgentType(DbServed)
            .AddPluginCatalog();

    private static readonly IReadOnlyList<RepoFile> Repo = new List<RepoFile>
    {
        new("Agent/index.json",
            """
            {"$type":"MeshNode","id":"Agent","namespace":"","path":"Agent","mainNode":"Agent",
             "name":"Agents","nodeType":"Space","state":"Active",
             "content":{"$type":"PluginManifest","description":"The standard agents we ship.",
                        "preInstalled":true}}
            """),
        new("Agent/Helper.json",
            """
            {"$type":"MeshNode","id":"Helper","namespace":"Agent","path":"Agent/Helper",
             "mainNode":"Agent/Helper","name":"Helper","nodeType":"Agent","state":"Active",
             "content":{"$type":"AgentConfiguration","id":"Helper","instructions":"You help."}}
            """),
    };

    private static NodeRepoPackageSource Source()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-ghost", Repo));
        return new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");
    }

    /// <summary>The moment the ghost was first (half-)written — the lineage marker the repair must keep.</summary>
    private static readonly DateTimeOffset GhostCreatedAt =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    /// <summary>
    /// The version the ghost row carries. NOT cosmetic — it is the second half of what makes a
    /// ghost unrepairable: <c>MonotonicWriteGuardStorageAdapter</c> refuses any write landing below
    /// the stored version, and a hub that cannot hydrate the content-less row mints from 0. A ghost
    /// at version 0 would repair even with the bug in place; a real one never sits at 0, because it
    /// is the residue of a node that was genuinely written.
    /// </summary>
    private const long GhostVersion = 7;

    [Fact(Timeout = 180_000)]
    public async Task InstallingOverAGhostRoot_RepairsItInPlace_AndPublishesThePartition()
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

        // ── the ghost: content-less, type-less, grant-less, written straight to storage ──
        await storage.Write(
                new MeshNode("Agent") { CreatedDate = GhostCreatedAt, Version = GhostVersion },
                Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();

        var ghost = await Read("Agent");
        ghost.Should().NotBeNull();
        ghost!.Content.Should().BeNull("a ghost root is the residue of a half-completed create");

        // ── the blocker, reproduced: the ordinary route back is CLOSED. The durable row is there,
        //    so create refuses — which is what left the production portal with no way to restore
        //    its agent catalog by hand.
        var create = await Record.ExceptionAsync(() => Mesh.ServiceProvider
            .GetRequiredService<IMeshService>()
            .CreateNode(new MeshNode("Agent") { NodeType = "Space", Name = "Agents" })
            .FirstAsync().ToTask());
        create.Should().BeOfType<InvalidOperationException>();
        create!.Message.Should().Contain("already exists");

        // ── the repair: install the package that owns the partition ──
        var source = Source();
        var packages = await source.ListPackages("HEAD").FirstAsync().ToTask();
        var agents = packages.Single(p => p.Id == "Agent");
        agents.PreInstalled.Should().BeTrue("the manifest is what declares this a default-install package");

        var files = await source.FetchPackageFiles(agents, "HEAD").FirstAsync().ToTask();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => PackageInstaller.Install(Mesh, agents, files, "commit-ghost"))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();

        // ── converged: a healthy, typed, content-carrying root … ──
        var repaired = await Read("Agent");
        repaired.Should().NotBeNull();
        repaired!.NodeType.Should().Be("Space");
        repaired.Name.Should().Be("Agents");
        repaired.Content.Should().NotBeNull();
        repaired.State.Should().Be(MeshNodeState.Active);

        // … repaired IN PLACE, not replaced. Two independent proofs that this is the SAME node's
        // lineage rather than a fresh one shadowing it:
        //  • the ghost's own creation stamp survives;
        //  • the version counter CONTINUED from the ghost's (the repair minted above it, so no
        //    forked lineage — which is also the only way the write got past the monotonic write
        //    guard at all).
        repaired.CreatedDate.Should().Be(GhostCreatedAt);
        repaired.Version.Should().BeGreaterThan(GhostVersion);

        // … with its content mounted …
        (await Read("Agent/Helper")).Should().NotBeNull();

        // … and PUBLISHED by the installer, which is what makes it readable again at all.
        var policy = await Read($"Agent/{PackageInstaller.PartitionPolicyId}");
        policy.Should().NotBeNull();
        policy!.ContentAs<PartitionAccessPolicy>(Mesh.JsonSerializerOptions)!.PublicRead
            .Should().BeTrue();
        await Mesh.GetEffectivePermissions("Agent", PlainUser)
            .Should().Match(p => p.HasFlag(Permission.Read));

        // Exactly ONE node answers for the path — the ghost was healed, not shadowed by a duplicate.
        await Query("path:Agent", PlainUser)
            .Should().Within(TimeSpan.FromSeconds(30))
            .Match(change => change.Items.Count(n => n.Path == "Agent") == 1);
    }

    [Fact(Timeout = 180_000)]
    public async Task RepairIsIdempotent_ASecondInstallWritesNothing()
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await storage.Write(
                new MeshNode("Agent") { CreatedDate = GhostCreatedAt, Version = GhostVersion },
                Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();

        var source = Source();
        var packages = await source.ListPackages("HEAD").FirstAsync().ToTask();
        var agents = packages.Single(p => p.Id == "Agent");
        var files = await source.FetchPackageFiles(agents, "HEAD").FirstAsync().ToTask();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        IObservable<InstallResult> Install() => Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => PackageInstaller.Install(Mesh, agents, files, "commit-ghost"));

        var first = await Install().FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();
        first.Written.Should().BeGreaterThan(0);

        // The self-update shape over a REPAIRED root: nothing left to do, so nothing is written —
        // the repair must not become a per-boot rewrite of the partition it healed.
        var second = await Install().FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();
        second.Written.Should().Be(0);
    }

    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();

    private IObservable<QueryResultChange<MeshNode>> Query(string query, string identity) =>
        Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(query, identity));
}
