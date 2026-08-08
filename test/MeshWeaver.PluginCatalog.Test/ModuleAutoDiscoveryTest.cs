#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Auto-discovery of a configured plugin repo's modules (#833).
///
/// <para>The property under test is the one the issue is actually about: on a real instance modules
/// arrive as <c>{Space}/_GitSync</c> entries, and a module with no entry is <b>not on the mesh with
/// nothing saying so</b>. So: with discovery on, an absent module must become VISIBLE; with auto-sync
/// on it must be PROVISIONED end to end; and with both off nothing whatsoever may happen.</para>
///
/// <para>🚨 The security property is that provisioning mints NO human grant. Creating a Space the
/// ordinary way makes its creator Admin — on a partition the repo owns, which is forbidden and which
/// <see cref="SystemOwnedAccessRetractionHandler"/> exists to clean up after. Doing it as SYSTEM means
/// there is nothing to clean up: the tests assert the Space's <c>CreatedBy</c> is
/// <c>system-security</c> and that the triggering user holds no <c>_Access</c> grant at all.</para>
/// </summary>
public class ModuleAutoDiscoveryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/acme/plugins";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddPluginCatalog()
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // The in-memory repo replaces the real client (last registration wins), so the
                // whole provisioning path — including the first import — runs offline.
                services.AddSingleton<IGitHubRepoClient>(Repo);
                return services;
            });

    /// <summary>
    /// A node-native plugin repo with three modules: two free (<c>Planning</c>, <c>Ifrs17</c> — the
    /// very pair that was silently absent from memex) and one PRICED, which must never be
    /// auto-provisioned.
    /// </summary>
    private readonly StubRepoClient Repo = new(RepoUrl, "sha-1",
    [
        new("Planning/index.json",
            """{"$type":"MeshNode","id":"Planning","namespace":"","path":"Planning","mainNode":"Planning","name":"Planning","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Planning module."}}"""),
        new("Planning/Overview.json",
            """{"$type":"MeshNode","id":"Overview","namespace":"Planning","path":"Planning/Overview","mainNode":"Planning/Overview","name":"Overview","nodeType":"Markdown","state":"Active","content":"# Planning"}"""),
        new("Ifrs17/index.json",
            """{"$type":"MeshNode","id":"Ifrs17","namespace":"","path":"Ifrs17","mainNode":"Ifrs17","name":"IFRS 17","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"IFRS 17 module."}}"""),
        new("Premium/index.json",
            """{"$type":"MeshNode","id":"Premium","namespace":"","path":"Premium","mainNode":"Premium","name":"Premium","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Paid module.","price":99,"currency":"CHF"}}"""),
    ]);

    private ModuleDiscoveryService Discovery =>
        Mesh.ServiceProvider.GetRequiredService<ModuleDiscoveryService>();

    private ConfiguredPackageSource Source(bool autoSync) =>
        new(new NodeRepoPackageSource(Repo.Fetch, RepoUrl), "main", "plugins")
        {
            RepoPath = RepoUrl,
            AutoDiscover = true,
            AutoSync = autoSync,
        };

    // ── discovery only ────────────────────────────────────────────────────────

    /// <summary>
    /// AutoDiscover on, AutoSync off: every absent module is REPORTED and nothing is written into a
    /// Space. This is the fix for the core complaint — absence stops being invisible — while an
    /// instance that has not opted into unattended provisioning still gets no surprise Spaces.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AutoDiscoverOnly_ReportsTheAbsentModules_AndCreatesNothing()
    {
        var record = await Discovery.Scan(Source(autoSync: false), "main")
            .Should().Within(120.Seconds()).Emit();

        record.RepositoryUrl.Should().Be(RepoUrl);
        record.AutoSync.Should().BeFalse();
        Status(record, "Planning").Should().Be(ModuleDiscoveryStatus.Discovered,
            "the repo ships Planning and this instance does not carry it — that must be visible");
        Status(record, "Ifrs17").Should().Be(ModuleDiscoveryStatus.Discovered);
        record.Modules.Single(m => m.Id == "Planning").Detail.Should().NotBeNullOrWhiteSpace(
            "a discovered-but-absent module must say WHY nothing happened, never be a silent skip");

        // The record is a real, readable node — that is the whole surface.
        var node = await ReadAsSystem(ModuleDiscovery.PathFor(RepoUrl));
        node.Should().NotBeNull();
        node!.NodeType.Should().Be(ModuleDiscovery.NodeType);

        // …and NOTHING was provisioned.
        (await ReadAsSystem("Planning")).Should().BeNull("discovery alone must never create a Space");
        (await ReadAsSystem("Planning/_GitSync")).Should().BeNull();
    }

    // ── discovery + provisioning ──────────────────────────────────────────────

    /// <summary>
    /// AutoSync on: the missing module is provisioned end to end — Space, sync entry, content — and
    /// the user whose session triggered the scan gains NOTHING. Re-running is a no-op.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AutoSyncOn_ProvisionsAsSystem_WithoutGrantingTheTriggeringUserAdmin()
    {
        // The MACHINE identity the first import authenticates with. In production that is the GitHub
        // App installation token (GitHubSyncService.ResolveAuth's fallback for a server-side sync
        // with no personal login); here it is a stored credential for the same principal, so the
        // import runs offline against the stub repo instead of needing a real App.
        await Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>()
            .Save(WellKnownUsers.System, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
            .Timeout(60.Seconds()).ToTask();

        // A signed-in user is in scope for the whole scan — the shape a webhook-behind-a-session or
        // an admin-triggered rescan has. They must still end up with no grant on the new partition.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = TestUsers.Admin.ObjectId,
            Name = TestUsers.Admin.Name,
        });

        try
        {
            var record = await Discovery.Scan(Source(autoSync: true), "main")
                .Should().Within(240.Seconds()).Emit();

            Status(record, "Planning").Should().Be(ModuleDiscoveryStatus.Provisioned);
            Status(record, "Ifrs17").Should().Be(ModuleDiscoveryStatus.Provisioned);
            record.Modules.Single(m => m.Id == "Planning").ProvisionedAt.Should().NotBeNull(
                "the record must answer 'what appeared, and when?'");

            // 1. The Space exists — and SYSTEM made it.
            var space = await ReadAsSystem("Planning");
            space.Should().NotBeNull();
            space!.NodeType.Should().Be(GitHubSyncService.SpaceNodeType);
            space.CreatedBy.Should().Be(WellKnownUsers.System,
                "a repo-owned partition must be created by the platform, never by whoever happened "
                + "to trigger the scan — that is what stops a human Admin grant being minted");

            // 2. The sync entry is there, pointing at the module's folder, import-only.
            var config = await Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>()
                .ReadConfig("Planning").Where(c => c is not null).Should().Within(60.Seconds()).Emit();
            config!.RepositoryUrl.Should().Be(RepoUrl);
            config.Subdirectory.Should().Be("Planning");
            config.Branch.Should().Be("main");
            config.Direction.Should().Be(SyncDirection.ImportOnly,
                "an unattended consumer must never be wired to push back into a shared plugin repo");
            config.CreateRepoIfMissing.Should().BeFalse();

            // 3. 🚨 No grant for the triggering user — the property the whole design exists for.
            var grants = await AccessGrantPaths("Planning");
            grants.Should().NotContain(p => p.Contains(TestUsers.Admin.ObjectId!, StringComparison.OrdinalIgnoreCase),
                "provisioning as SYSTEM must mint no personal Admin grant on a repo-owned partition");

            // 4. The first import actually ran — the module's content is on the mesh.
            var overview = await Mesh.GetMeshNode("Planning/Overview", TimeSpan.FromSeconds(60))
                .Where(n => n is not null).Should().Within(90.Seconds()).Emit();
            overview!.NodeType.Should().Be("Markdown");

            // 5. Re-running is a NO-OP: the module now has a sync entry, so it is reported as
            //    Synced and nothing is written again.
            var second = await Discovery.Scan(Source(autoSync: true), "main")
                .Should().Within(120.Seconds()).Emit();
            Status(second, "Planning").Should().Be(ModuleDiscoveryStatus.Synced,
                "a module that already syncs must never be re-provisioned");
            Status(second, "Ifrs17").Should().Be(ModuleDiscoveryStatus.Synced);
        }
        finally
        {
            accessService.SetCircuitContext(null);
        }
    }

    /// <summary>
    /// A PRICED module is never swept in (#830). An unattended scan has no authorizing principal, so
    /// the free-vs-commercial gate refuses it — and the refusal is RECORDED rather than silent.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task PricedModule_IsRefused_NotAutoProvisioned()
    {
        var record = await Discovery.Scan(Source(autoSync: true), "main")
            .Should().Within(240.Seconds()).Emit();

        Status(record, "Premium").Should().Be(ModuleDiscoveryStatus.Refused);
        record.Modules.Single(m => m.Id == "Premium").Detail
            .Should().Contain("Global Admin", "the refusal must name its reason, not vanish");
        (await ReadAsSystem("Premium")).Should().BeNull(
            "a commercial module must never appear on an instance without an entitled admin");
    }

    /// <summary>
    /// A path already occupied by somebody's Space is left ALONE. Adopting it would wire a
    /// <c>_GitSync</c> onto their partition, which makes it system-owned and retracts their access —
    /// an unattended scan may not do that to content it did not create.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task OccupiedPath_IsReported_NeverAdopted()
    {
        await NodeFactory.CreateNode(new MeshNode("Planning")
        {
            NodeType = GitHubSyncService.SpaceNodeType,
            Name = "Somebody's own Planning",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(60.Seconds()).ToTask();

        var record = await Discovery.Scan(Source(autoSync: true), "main")
            .Should().Within(240.Seconds()).Emit();

        Status(record, "Planning").Should().Be(ModuleDiscoveryStatus.Occupied);
        (await ReadAsSystem("Planning/_GitSync")).Should().BeNull(
            "wiring a sync entry onto an existing Space would retract its owner's access");
    }

    // ── the flags themselves ──────────────────────────────────────────────────

    /// <summary>
    /// Both flags off — the default — means literally nothing runs: the source is filtered out
    /// before anything is listed, queried or written. Only a literal <c>true</c> opts in, so a typo
    /// in a Helm value can never be what turns unattended Space creation on.
    /// </summary>
    [Fact]
    public void BothFlagsOff_IsTheDefault_AndNothingIsEvenEnumerated()
    {
        var unset = Configure(("PluginCatalog:Sources:0:RepoPath", RepoUrl));
        var sources = PackageSources.FromConfiguration(Mesh, unset);
        sources.Should().ContainSingle();
        sources[0].AutoDiscover.Should().BeFalse("discovery is opt-in");
        sources[0].AutoSync.Should().BeFalse();
        Discovery.DiscoverableSources(unset).Should().BeEmpty(
            "a source that did not opt in must never be scanned at all");

        var typo = Configure(
            ("PluginCatalog:Sources:0:RepoPath", RepoUrl),
            ("PluginCatalog:Sources:0:AutoDiscover", "yes"),
            ("PluginCatalog:Sources:0:AutoSync", "1"));
        PackageSources.FromConfiguration(Mesh, typo)[0].AutoDiscover.Should().BeFalse(
            "only a literal 'true' may enable unattended provisioning");
        PackageSources.FromConfiguration(Mesh, typo)[0].AutoSync.Should().BeFalse();
    }

    /// <summary>The flags are read off the SAME per-source config the repo/ref come from — the repo
    /// is the unit of configuration (#833), and the values ride in the deployment's Helm values.</summary>
    [Fact]
    public void FlagsAreReadPerSource_AlongsideTheRepoAndRef()
    {
        var config = Configure(
            ("PluginCatalog:Sources:0:RepoPath", RepoUrl),
            ("PluginCatalog:Sources:0:Subdir", "modules"),
            ("PluginCatalog:Sources:0:Ref", "release"),
            ("PluginCatalog:Sources:0:Name", "plugins"),
            ("PluginCatalog:Sources:0:AutoDiscover", "true"),
            ("PluginCatalog:Sources:0:AutoSync", "true"),
            ("PluginCatalog:Sources:1:RepoPath", "https://github.com/acme/education"),
            ("PluginCatalog:Sources:1:Name", "education"));

        var sources = PackageSources.FromConfiguration(Mesh, config);
        sources.Should().HaveCount(2);

        var plugins = sources.Single(s => s.Name == "plugins");
        plugins.RepoPath.Should().Be(RepoUrl);
        plugins.Subdir.Should().Be("modules");
        plugins.GitRef.Should().Be("release");
        plugins.AutoDiscover.Should().BeTrue();
        plugins.AutoSync.Should().BeTrue();

        sources.Single(s => s.Name == "education").AutoDiscover.Should().BeFalse(
            "the flags are PER SOURCE — one repo opting in must not enrol another");

        Discovery.DiscoverableSources(config).Select(s => s.Name).Should().Equal("plugins");
    }

    /// <summary>The record path keys on owner.repo, so a repo's build record
    /// (<c>Admin/_Build/{owner}.{repo}</c>) and its discovery record sit side by side.</summary>
    [Theory]
    [InlineData("https://github.com/acme/plugins", "Admin/_Discovery/acme.plugins")]
    [InlineData("https://github.com/acme/plugins.git", "Admin/_Discovery/acme.plugins")]
    [InlineData("git@github.com:acme/plugins.git", "Admin/_Discovery/acme.plugins")]
    [InlineData("/checkouts/acme/plugins/", "Admin/_Discovery/acme.plugins")]
    public void DiscoveryRecordPath_KeysOnOwnerAndRepo(string repo, string expected)
        => ModuleDiscovery.PathFor(repo).Should().Be(expected);

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ModuleDiscoveryStatus Status(ModuleDiscovery record, string id) =>
        record.Modules.Single(m => m.Id == id).Status;

    private static IConfiguration Configure(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    /// <summary>Authoritative persistence read as System — the discovery record and the freshly
    /// provisioned partitions carry no user grants by construction.</summary>
    private async Task<MeshNode?> ReadAsSystem(string path)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        return await Observable.Using(
                () => access.ImpersonateAsSystem(),
                _ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Take(1).Timeout(30.Seconds()).ToTask();
    }

    private async Task<IReadOnlyList<string>> AccessGrantPaths(string partition)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var children = await Observable.Using(
                () => access.ImpersonateAsSystem(),
                _ => storage.ListChildPaths($"{partition}/_Access"))
            .Take(1)
            .Catch<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths), Exception>(
                _ => Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], [])))
            .Timeout(30.Seconds()).ToTask();
        return children.NodePaths?.ToList() ?? [];
    }
}
