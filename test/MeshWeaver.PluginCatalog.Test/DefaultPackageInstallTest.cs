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

    /// <summary>
    /// THE default-install service — the single path that decides what installs at boot. The test
    /// drives the very same method the boot pass does, differing only in where the source list
    /// comes from.
    /// </summary>
    private InstanceAutoRegistrationService Installer() =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

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

        await Installer()
            .InstallFrom([new ConfiguredPackageSource(Catalog(), "HEAD", "test")],
                baseline: false, wanted)
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

        await Installer()
            .InstallFrom([new ConfiguredPackageSource(Catalog(), "HEAD", "test")],
                baseline: false, wanted)
            .Should().Within(120.Seconds()).Emit();

        var records = (await InstalledRecords().Should().Emit()).Select(n => n.Id).ToList();
        records.Should().Contain("Store");
        records.Should().NotContain("Essentials", "only the named package is a default");
    }

    /// <summary>
    /// A dependency installs BEFORE its dependents. The shape is the real one that broke the first
    /// live run: Chess declares <c>Store@^1.0.0</c> and <c>Training@^1.0.0</c>, and catalog order is
    /// alphabetical — so without ordering Chess installs first and dies with
    /// "NodeType(s) not registered: Training/Tour".
    /// </summary>
    [Fact]
    public void DependenciesInstallBeforeTheirDependents()
    {
        var catalogOrder = new[]
        {
            new PackageManifest { Id = "Chess", Requires = ["Store@^1.0.0", "Training@^1.0.0"] },
            new PackageManifest { Id = "Essentials" },
            new PackageManifest { Id = "Store" },
            new PackageManifest { Id = "Training", Requires = ["Store@^1.0.0"] },
        };

        var ordered = PackageDependencyGraph
            .InDependencyOrder(catalogOrder, NullLogger.Instance)
            .Select(p => p.Id).ToList();

        ordered.Should().HaveCount(4, "ordering must not drop or duplicate a package");
        ordered.IndexOf("Store").Should().BeLessThan(ordered.IndexOf("Training"));
        ordered.IndexOf("Store").Should().BeLessThan(ordered.IndexOf("Chess"));
        ordered.IndexOf("Training").Should().BeLessThan(ordered.IndexOf("Chess"));
    }

    [Fact]
    public void DependencyCycle_StillInstallsEveryPackageOnce()
    {
        // A cycle is a repo authoring error, but it must still yield every package exactly once
        // rather than hang, drop packages, or recurse forever. Deliberately order-agnostic: the
        // sort drops the back edge and the order WITHIN a cycle is unspecified (see
        // PackageDependencyGraph.InDependencyOrder's remarks), which is why this sorts before
        // asserting.
        var cyclic = new[]
        {
            new PackageManifest { Id = "A", Requires = ["B"] },
            new PackageManifest { Id = "B", Requires = ["A"] },
        };

        var ordered = PackageDependencyGraph
            .InDependencyOrder(cyclic, NullLogger.Instance).Select(p => p.Id).OrderBy(x => x).ToList();

        ordered.Should().HaveCount(2);
        ordered.Should().Contain("A");
        ordered.Should().Contain("B");
    }

    [Fact]
    public void UngrantedDependency_IsIgnoredRatherThanBlocking()
    {
        // Depending on something this instance was not granted must not drop the dependent — it
        // may well install fine, and there is nothing to order against.
        var packages = new[] { new PackageManifest { Id = "Store", Requires = ["PaidCourse@^1.0.0"] } };

        var ordered = PackageDependencyGraph
            .InDependencyOrder(packages, NullLogger.Instance).Select(p => p.Id).ToList();
        ordered.Should().HaveCount(1);
        ordered.Should().Contain("Store");
    }

    /// <summary>
    /// A REGISTRY installing from its OWN configured source matches by the source's configured
    /// NAME, even though nothing stamped <see cref="PackageManifest.Source"/> on the way.
    ///
    /// <para>🚨 The live regression this pins: <c>Source</c> was stamped only by the registry's
    /// HTTP merge, so an instance reading its own sources directly (no HTTP hop) saw null on every
    /// package and <c>Plugins/*</c> matched nothing — a green deploy that installed zero plugins.
    /// The lister always knows which source it read from, so it stamps.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task RegistryInstallingFromItsOwnSource_MatchesByConfiguredName()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-local", Repo));
        // No SourceStampingCatalog wrapper — the raw source, exactly as a local registry reads it.
        var unstamped = new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");

        await Installer()
            // The source is CONFIGURED as "Plugins"; nothing stamped the packages.
            .InstallFrom([new ConfiguredPackageSource(unstamped, "HEAD", "Plugins")],
                baseline: false, [PluginGrantEntry.TryParse("Plugins/*")!])
            .Should().Within(120.Seconds()).Emit();

        var records = (await InstalledRecords().Should().Emit()).Select(n => n.Id).ToList();
        records.Should().Contain("Store", "a registry must install from its own source");
        records.Should().Contain("Essentials");
    }

    /// <summary>
    /// The seed ledger must NOT be typed as a Package.
    ///
    /// <para>🚨 It lives in the Plugins partition but is bookkeeping, not an install record, so
    /// typing it <c>Package</c> puts it in every query that enumerates installed packages by node
    /// type — the freshness probe, ModuleDiscovery's instance state, any inventory UI. The tell
    /// was that every verification query written against this feature had to exclude it by id; a
    /// filter you must repeat at each call site means the type is wrong.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task TheLedgerIsNotAnInstalledPackage()
    {
        await Installer()
            .InstallFrom([new ConfiguredPackageSource(Catalog(), "HEAD", "Plugins")],
                baseline: false, [PluginGrantEntry.TryParse("Plugins/Store")!])
            .Should().Within(120.Seconds()).Emit();

        // InstalledRecords() queries nodeType:Package — the ledger must be invisible to it, with
        // NO id-based exclusion applied here.
        var records = (await InstalledRecords().Should().Emit()).Select(n => n.Id).ToList();
        records.Should().Contain("Store");
        records.Should().NotContain("_DefaultInstallLedger",
            "the ledger is bookkeeping and must never surface as an installed package");
    }

    /// <summary>
    /// 🚨 The ledger must actually be WRITABLE — its node type has to be registered, not merely its
    /// content type.
    ///
    /// <para>Registering <c>DefaultInstallLedger</c> on the TypeRegistry (so the content
    /// round-trips) is only half the job: <c>CreateNode</c> validates <c>node.NodeType</c> against
    /// the registered NodeType MeshNodes. Without the node type the ledger write failed every boot
    /// with <c>NodeType 'DefaultInstallLedger' is not registered</c> — and because
    /// <c>RecordSeeded</c> deliberately swallows a ledger failure as a warning (a lost ledger must
    /// never fail a boot), the failure was SILENT and the ledger stayed permanently empty.
    /// Measured on memex 2026-08-10 at 07:16:45 and 11:46:24.</para>
    ///
    /// <para>What that cost: <c>SeedLedger()</c> then always answers "nothing has ever been
    /// seeded", so every boot re-ran the FULL default install — upserting every plugin partition
    /// root — instead of the repair-only pass the design describes, and "a failed package is
    /// retried next boot" could never be told apart from "re-do work already done".</para>
    ///
    /// <para>The existing <see cref="TheLedgerIsNotAnInstalledPackage"/> could not catch this: it
    /// asserts the ledger is ABSENT from the package query, which a never-written ledger satisfies
    /// perfectly. This asserts presence — the write goes through the same
    /// <c>CreateOrUpdateNode</c> call <c>RecordSeeded</c> makes.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task TheLedgerNodeTypeIsRegistered_SoTheSeedLedgerCanBeWritten()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        var ledger = new MeshNode("_DefaultInstallLedger", PackageInstaller.InstalledPartition)
        {
            Name = "Default install ledger",
            NodeType = InstanceAutoRegistrationService.LedgerNodeType,
            State = MeshNodeState.Active,
            Content = new DefaultInstallLedger
            {
                Seeded = ["Store"],
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };

        // Exactly RecordSeeded's write. Red-before this fix: OnError with
        // "NodeType 'DefaultInstallLedger' is not registered".
        await Observable.Using(() => access.ImpersonateAsSystem(), _ => mesh.CreateOrUpdateNode(ledger))
            .Should().Within(60.Seconds()).Emit();

        var written = await Observable.Using(
                () => access.ImpersonateAsSystem(),
                _ => Mesh.GetMeshNode(
                    $"{PackageInstaller.InstalledPartition}/_DefaultInstallLedger",
                    TimeSpan.FromSeconds(30)))
            .Where(n => n is not null)
            .Should().Emit();

        written!.NodeType.Should().Be(InstanceAutoRegistrationService.LedgerNodeType);
        written.ContentAs<DefaultInstallLedger>(Mesh.JsonSerializerOptions)!
            .Seeded.Should().Contain("Store",
                "a ledger that cannot record what was seeded makes every boot re-run the whole install");
    }

    [Fact(Timeout = 180_000)]
    public async Task UnstampedCatalog_InstallsNothing_FailsClosed()
    {
        // A pattern naming a DIFFERENT source than the one configured ("Plugins/*" against a
        // source configured as "test"): the right answer is to install NOTHING rather than guess
        // that the operator meant this source. Fails closed — and UnmatchablePatterns reports it
        // loudly, because silence here is what made a green deploy install zero plugins.
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-default", Repo));
        var unstamped = new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");

        await Installer()
            .InstallFrom([new ConfiguredPackageSource(unstamped, "HEAD", "test")],
                baseline: false, [PluginGrantEntry.TryParse("Plugins/*")!])
            .Should().Within(60.Seconds()).Emit();

        (await InstalledRecords().Should().Emit()).Should().BeEmpty();
    }
}
