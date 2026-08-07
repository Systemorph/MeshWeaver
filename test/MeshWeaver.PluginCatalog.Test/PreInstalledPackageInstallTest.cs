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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// THE DEFAULT INSTALL (#902). A package whose manifest declares <c>preInstalled</c> must be
/// installed on every instance automatically, published read-only to everyone BY THE INSTALLER, and
/// left completely alone on the next boot.
///
/// <para>This is the regression that took the platform's agent catalog off the production portal:
/// <c>MeshWeaver.Plugins</c> shipped <c>"preInstalled": true</c> on its <c>Agent</c> and
/// <c>Skill</c> roots, nothing in the platform ever read the flag, and the catalogs were therefore
/// only ever present where somebody had hand-placed a <c>_GitSync</c> + <c>_Policy</c> pair. The
/// <c>Agent</c> partition never got that hand-treatment, so when its content went there was no
/// mechanism anywhere that could bring it back and every user saw an empty agent picker.</para>
///
/// <para>The fixture is built on <see cref="MonolithMeshTestBase.ConfigureMeshBase"/>, NOT the
/// default <c>ConfigureMesh</c>: the latter seeds a root-scope <c>Public → Admin</c> grant that
/// would make every partition readable by everyone and void the whole point of asserting that the
/// installer established the public read.</para>
/// </summary>
public class PreInstalledPackageInstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A signed-in principal holding NOTHING — no role, no grant, anywhere.</summary>
    private const string PlainUser = "plain-user";

    /// <summary>The anonymous reader. <c>""</c> is the explicit-Anonymous marker on a query.</summary>
    private const string Anonymous = "";

    /// <summary>
    /// The partitions the platform's own catalogs live in. Declared DB-served exactly as the
    /// deployments do (<c>Features:StaticRepoSync:Partitions</c>) — otherwise <c>AddAgentType</c> /
    /// <c>AddSkillType</c> put a READ-ONLY static provider in front of them and no install could
    /// write a single node.
    /// </summary>
    private static readonly IReadOnlySet<string> DbServed =
        new HashSet<string>(StringComparer.Ordinal) { "Agent", "Skill" };

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddAgentType(DbServed)
            .AddSkillType(DbServed)
            .AddPluginCatalog()
            // The source seam: a package source registered in DI joins the default install. In
            // production this is the instance's configured git sources / the registries it
            // consumes; here it is the repo below, so the pass is exercised end to end without a
            // network.
            .ConfigureServices(services => services.AddSingleton<IPackageSource>(_ => Source()));

    // A node-native plugin repo in the shape MeshWeaver.Plugins ships: two PRE-INSTALLED catalogs
    // (the platform's agents and skills — same ids, same partitions, same `preInstalled` placement
    // inside the root's content) and one ordinary package that declares nothing and must therefore
    // NOT be installed by the default pass.
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
        new("Skill/index.json",
            """
            {"$type":"MeshNode","id":"Skill","namespace":"","path":"Skill","mainNode":"Skill",
             "name":"Skills","nodeType":"Space","state":"Active",
             "content":{"$type":"PluginManifest","description":"The standard skills we ship.",
                        "preInstalled":true}}
            """),
        new("Skill/demo.json",
            """
            {"$type":"MeshNode","id":"demo","namespace":"Skill","path":"Skill/demo",
             "mainNode":"Skill/demo","name":"demo","nodeType":"Skill","state":"Active",
             "content":{"$type":"SkillDefinition","description":"A demo skill."}}
            """),
        new("Paid/index.json",
            """
            {"$type":"MeshNode","id":"Paid","namespace":"","path":"Paid","mainNode":"Paid",
             "name":"Paid Plugin","nodeType":"Space","state":"Active",
             "content":{"$type":"PluginManifest","description":"Bought, not shipped."}}
            """),
        new("Paid/Doc.json",
            """
            {"$type":"MeshNode","id":"Doc","namespace":"Paid","path":"Paid/Doc",
             "mainNode":"Paid/Doc","name":"Doc","nodeType":"Markdown","state":"Active",
             "content":"# Paid"}
            """),
    };

    private static NodeRepoPackageSource Source()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-preinstall", Repo));
        return new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");
    }

    [Fact(Timeout = 180_000)]
    public async Task FreshMesh_InstallsExactlyThePreInstalledPackages_AndNothingElse()
    {
        var summary = await BootPass();

        // The default install is EXACTLY the packages whose manifest declares preInstalled — an
        // ordinary package must never be installed behind the operator's back.
        summary.Packages.Should().Equal("Agent", "Skill");
        summary.Installed.Should().Be(2);
        summary.Failed.Should().Be(0);

        // The install RECORDS — what the About tab and the store list as installed, next to
        // MeshWeaver Essentials.
        var agents = await Read($"{PackageInstaller.InstalledPartition}/Agent");
        agents.Should().NotBeNull();
        var skills = await Read($"{PackageInstaller.InstalledPartition}/Skill");
        skills.Should().NotBeNull();

        // The content actually landed at its canonical paths.
        (await Read("Agent")).Should().NotBeNull();
        (await Read("Agent/Helper")).Should().NotBeNull();
        (await Read("Skill/demo")).Should().NotBeNull();

        // … and the package that declared nothing was left alone, root and all.
        (await Read($"{PackageInstaller.InstalledPartition}/Paid")).Should().BeNull();
        (await Read("Paid")).Should().BeNull();
        (await Read("Paid/Doc")).Should().BeNull();
    }

    [Fact(Timeout = 180_000)]
    public async Task Installer_PublishesEachPreInstalledPartition_ReadOnlyToEveryone()
    {
        await BootPass();

        // The policy is written BY THE INSTALLER — not by a hand-placed node, which is exactly how
        // Skill survived on the production portal while Agent did not.
        foreach (var partition in new[] { "Agent", "Skill" })
        {
            var policy = await Read($"{partition}/{PackageInstaller.PartitionPolicyId}");
            policy.Should().NotBeNull($"the installer must publish the pre-installed partition {partition}");
            policy!.NodeType.Should().Be("PartitionAccessPolicy");
            policy.ContentAs<PartitionAccessPolicy>(Mesh.JsonSerializerOptions)!.PublicRead
                .Should().BeTrue();

            // A signed-in principal that holds NOTHING anywhere can read the partition. Without the
            // policy this is Permission.None: the content is written entirely under System (so no
            // creator grant is ever minted) into a partition nobody holds a role on.
            await Mesh.GetEffectivePermissions(partition, PlainUser)
                .Should().Match(p => p.HasFlag(Permission.Read));
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task PreInstalledAgentsAndSkills_AreReachable_ForANonAdminAndAnonymously()
    {
        await BootPass();

        // THE sglauser regression, in the picker's own words: the canonical agent-registry query
        // (AgentPickerProjection — the ONE string the composer, the /agent picker and the engine's
        // agent selection all issue) must return the shipped agent for a plain user AND for an
        // anonymous visitor.
        var agentQuery = AgentPickerProjection.BuildAgentQuery(userPath: $"{PlainUser}/Agent");
        foreach (var identity in new[] { PlainUser, Anonymous })
            await Query(agentQuery, identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(change => change.Items.Any(n => n.Path == "Agent/Helper"),
                    $"'{identity}' must see the platform's shipped agents in the picker");

        // Same for the skill menu.
        var skillQuery = $"namespace:{SkillNodeType.RootNamespace} nodeType:{SkillNodeType.NodeType}";
        foreach (var identity in new[] { PlainUser, Anonymous })
            await Query(skillQuery, identity)
                .Should().Within(TimeSpan.FromSeconds(30))
                .Match(change => change.Items.Any(n => n.Path == "Skill/demo"),
                    $"'{identity}' must see the platform's shipped skills");
    }

    [Fact(Timeout = 180_000)]
    public async Task SecondPass_WritesNothing()
    {
        await BootPass();

        // The self-update shape: every boot re-runs the default install. An instance already
        // carrying the packages must do a catalog listing and NOTHING else — no rewrite, no version
        // bump, no recompile.
        var second = await Mesh.ServiceProvider.GetRequiredService<PreInstalledPackageService>()
            .EnsureDefaults().FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();

        second.Installed.Should().Be(0, "a re-reconcile of unchanged packages must write nothing");
        second.UpToDate.Should().Be(2);
        second.Failed.Should().Be(0);
    }

    /// <summary>
    /// The startup default install's own completion signal — no polling, no sleep: the hosted
    /// service publishes its outcome on an <c>AsyncSubject</c>, so this is the boot pass itself.
    /// </summary>
    private Task<PreInstallSummary> BootPass() =>
        Mesh.ServiceProvider.GetRequiredService<PreInstalledPackageService>()
            .Completed.FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();

    /// <summary>Authoritative single-node read straight off storage (never the lagging index).</summary>
    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();

    private IObservable<QueryResultChange<MeshNode>> Query(string query, string identity) =>
        Mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery(query, identity));
}
