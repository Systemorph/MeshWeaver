#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// THE OPT-OUT. The platform's baseline install is on by default — an operator running an
/// air-gapped or hand-curated instance must be able to turn it OFF, and
/// <c>PluginCatalog:InstallPreInstalledPackages=false</c> is that switch.
///
/// <para>The knob has to be proven by its EFFECT, not by reading the property back: a setting that
/// binds but is never consulted looks identical to a working one. So this fixture is the exact
/// fixture <see cref="PreInstalledPackageInstallTest"/> uses — same repo, same two
/// <c>preInstalled</c> packages, same sources — with the single difference that the knob is off.
/// That test asserts the packages DO install; this one asserts the same boot pass installs
/// NOTHING. Between them the switch is pinned in both positions.</para>
///
/// <para>🚨 Opting out must suppress the install WITHOUT breaking startup: the portal still has to
/// come up, the pass still has to complete, and <c>Completed</c> still has to emit — otherwise
/// anything waiting on the default install would hang forever on an opted-out instance.</para>
/// </summary>
public class PreInstalledOptOutTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly IReadOnlySet<string> DbServed =
        new HashSet<string>(StringComparer.Ordinal) { "Agent", "Skill" };

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddAgentType(DbServed)
            .AddSkillType(DbServed)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IPackageSource>(_ => Source())
                // The opt-out itself. Everything else about this mesh is identical to the fixture
                // that DOES install, so a difference in outcome can only come from this line.
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false }));

    // The same node-native repo shape MeshWeaver.Plugins ships: two catalogs that DECLARE
    // preInstalled. An opted-out instance must leave both of them alone.
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
    };

    private static NodeRepoPackageSource Source()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-optout", Repo));
        return new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");
    }

    [Fact(Timeout = 180_000)]
    public async Task OptedOut_InstallsNothing_ButStillCompletes()
    {
        // The boot pass must still COMPLETE — suppressing the install must not wedge startup or
        // leave the completion signal hanging.
        var summary = await Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>()
            .Completed.FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();

        summary.Packages.Should().BeEmpty("InstallPreInstalledPackages=false must select no packages");
        summary.Installed.Should().Be(0);
        summary.UpToDate.Should().Be(0);
        summary.Failed.Should().Be(0);

        // …and nothing landed: no install records, no content, no partition policy.
        (await Read($"{PackageInstaller.InstalledPartition}/Agent")).Should().BeNull();
        (await Read($"{PackageInstaller.InstalledPartition}/Skill")).Should().BeNull();
        (await Read("Agent/Helper")).Should().BeNull(
            "an opted-out instance must not receive the platform baseline's content");
        (await Read($"Agent/{PackageInstaller.PartitionPolicyId}")).Should().BeNull(
            "no install means no installer-written publication either");
    }

    /// <summary>Authoritative single-node read straight off storage (never the lagging index).</summary>
    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();
}
