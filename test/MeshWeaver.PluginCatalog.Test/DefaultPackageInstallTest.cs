#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// What a FRESH installation comes up with: <c>PluginCatalog:InstallByDefault</c> selects catalog
/// entries by <c>Source/Package</c> and installs them on first startup, so a new deployment is
/// usable instead of merely authorized.
///
/// <para>🚨 The security property under test is that the selection is SOURCE-SCOPED. An instance
/// is routinely granted both the platform repo AND paid content (course repos); "install what I'm
/// entitled to" would sweep the paid content in. A <c>Plugins/*</c> default must install the
/// platform packages and leave an <c>Education</c> package alone even though the same catalog
/// listing carries it.</para>
/// </summary>
public class DefaultPackageInstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph().AddPluginCatalog();

    // Two plugins from the platform repo (one of them the Store) and one from a course repo — the
    // exact shape of a real merged catalog: same listing, different provenance.
    private static readonly IReadOnlyList<RepoFile> Repo =
    [
        new("Store/index.json",
            """{"$type":"MeshNode","id":"Store","namespace":"","path":"Store","mainNode":"Store","name":"Store","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"The store.","minMeshVersion":"1.0.0"}}"""),
        new("Store/Welcome.json",
            """{"$type":"MeshNode","id":"Welcome","namespace":"Store","path":"Store/Welcome","mainNode":"Store/Welcome","name":"Welcome","nodeType":"Markdown","state":"Active","content":"# Welcome to the Store"}"""),
        new("Essentials/index.json",
            """{"$type":"MeshNode","id":"Essentials","namespace":"","path":"Essentials","mainNode":"Essentials","name":"Essentials","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Essentials.","minMeshVersion":"1.0.0"}}"""),
        new("PaidCourse/index.json",
            """{"$type":"MeshNode","id":"PaidCourse","namespace":"","path":"PaidCourse","mainNode":"PaidCourse","name":"Paid Course","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Paid content.","minMeshVersion":"1.0.0"}}"""),
    ];

    /// <summary>
    /// A catalog source in the shape the REGISTRY serves: real node-repo packages, each stamped
    /// with the source it came from (<see cref="PackageManifest.Source"/>) exactly as
    /// <c>PluginRegistryEndpoints</c> stamps them while merging its sources.
    /// </summary>
    private sealed class SourceStampingCatalog(NodeRepoPackageSource inner) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            inner.ListPackages(gitRef).Select(list => (IReadOnlyList<PackageManifest>)list
                .Select(p => p with { Source = p.Id == "PaidCourse" ? "Education" : "Plugins" })
                .ToList());

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            inner.FetchPackageFiles(package, gitRef);
    }

    private static IPackageSource Catalog()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-default", Repo));
        return new SourceStampingCatalog(
            new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins"));
    }

    private IObservable<IReadOnlyList<MeshNode>> InstalledRecords()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => access.ImpersonateAsSystem(),
                _ => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{PackageInstaller.InstalledPartition} scope:children "
                    + $"nodeType:{PackageInstaller.PackageNodeType}")))
            .Take(1)
            .Select(c => (IReadOnlyList<MeshNode>)c.Items.ToList());
    }

    [Fact(Timeout = 180_000)]
    public async Task PluginsStar_InstallsThePlatformRepo_AndLeavesPaidContentAlone()
    {
        var wanted = new[] { PluginGrantEntry.TryParse("Plugins/*")! };

        await InstanceAutoRegistrationService
            .InstallSelected(Mesh, Catalog(), "HEAD", wanted, NullLogger.Instance)
            .Should().Within(120.Seconds()).Emit();

        var records = (await InstalledRecords().Should().Emit())
            .Select(n => n.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();

        records.Should().Contain("Store", "the Store ships in the platform repo and is a default");
        records.Should().Contain("Essentials");
        records.Should().NotContain("PaidCourse",
            "a Plugins/* default must NEVER sweep in content from another source the instance may also be granted");

        // The content actually landed — an install record without its nodes would be a lie.
        var welcome = await Mesh.GetMeshNode("Store/Welcome", TimeSpan.FromSeconds(30))
            .Where(n => n is not null).Should().Emit();
        welcome!.NodeType.Should().Be("Markdown");
    }

    [Fact(Timeout = 180_000)]
    public async Task SinglePackageDefault_InstallsOnlyThatOne()
    {
        var wanted = new[] { PluginGrantEntry.TryParse("Plugins/Store")! };

        await InstanceAutoRegistrationService
            .InstallSelected(Mesh, Catalog(), "HEAD", wanted, NullLogger.Instance)
            .Should().Within(120.Seconds()).Emit();

        var records = (await InstalledRecords().Should().Emit()).Select(n => n.Id).ToList();
        records.Should().Contain("Store");
        records.Should().NotContain("Essentials", "only the named package is a default");
    }

    [Fact(Timeout = 180_000)]
    public async Task UnstampedCatalog_InstallsNothing_FailsClosed()
    {
        // A registry predating source-stamped entries: a Source/* default cannot match, and the
        // right answer is to install NOTHING rather than guess which source a package belongs to.
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-default", Repo));
        var unstamped = new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");

        await InstanceAutoRegistrationService
            .InstallSelected(Mesh, unstamped, "HEAD",
                [PluginGrantEntry.TryParse("Plugins/*")!], NullLogger.Instance)
            .Should().Within(60.Seconds()).Emit();

        (await InstalledRecords().Should().Emit()).Should().BeEmpty();
    }
}
