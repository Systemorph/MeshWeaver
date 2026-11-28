using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A package the registry PROVES retired is converged away; one merely absent from a listing
/// is not</b> (MeshWeaver#8619, <c>Doc/Architecture/RetiredPackageConvergence</c>). The pure rules
/// are pinned here; the removal runs against a real mesh below.
/// </summary>
public class RetiredPackageProofTest
{
    private static PackageManifest Pkg(string id) => new() { Id = id, Name = id, Source = "Plugins" };

    private static RegistryListing Listing(string[] listed, params RetiredPackage[] retired) =>
        new() { Packages = [.. listed.Select(Pkg)], Retired = [.. retired] };

    [Fact]
    public void AnExplicitRetiredEntry_IsProven()
    {
        var proven = RetiredPackageConvergence.Proven("Plugins",
            Listing(["Store"], new RetiredPackage { Id = "ContainerRegistry", Reason = "moved to the fleet registry" }));

        var one = Assert.Single(proven);
        Assert.Equal(("Plugins", "ContainerRegistry", "moved to the fleet registry"), (one.Source, one.PackageId, one.Reason));
    }

    /// <summary>The negative control. The package is simply MISSING from a (partial) listing and the
    /// registry declares nothing retired: nothing is proven, so nothing may ever be removed.</summary>
    [Fact]
    public void APackageMerelyAbsentFromAListing_IsNeverProven()
    {
        var partial = Listing(["Store"]);
        Assert.Empty(RetiredPackageConvergence.Proven("Plugins", partial));
        Assert.Empty(RetiredPackageConvergence.Proven("Plugins", new RegistryListing()));
    }

    [Fact]
    public void AnIdTheSameListingAlsoLists_IsNotRetired()
    {
        var proven = RetiredPackageConvergence.Proven("Plugins",
            Listing(["ContainerRegistry"], new RetiredPackage { Id = "ContainerRegistry" }));
        Assert.Empty(proven);
    }

    [Fact]
    public void ARetirementIsScopedToItsSource()
    {
        var composition = RetiredPackageConvergence.CompositionOf(
            RetiredPackageConvergence.Proven("Plugins", Listing([], new RetiredPackage { Id = "Marketing" })));
        Assert.Equal(["Plugins/Marketing"], composition.Retired.Select(p => p.Package));

        var records = new[]
        {
            new PackageManifest { Id = "Marketing", Source = "Plugins", TargetPartition = "Marketing" },
            new PackageManifest { Id = "Marketing", Source = "Elsewhere", TargetPartition = "Marketing" },
            new PackageManifest { Id = "Store", Source = "Plugins", TargetPartition = "Store" },
        };
        var decision = DeclaredDisabledRetirement.Decide(records, composition.Retired);
        Assert.Equal(["Plugins"], decision.Retire.Select(c => c.Record.Source!));
        Assert.Equal(["Marketing"], decision.Retire.Select(c => c.Record.Id));
    }

    [Fact]
    public void TheWire_CarriesRetired_AndIsByteIdenticalWhenNothingIsRetired()
    {
        var packages = new[] { Pkg("Store") };
        Assert.Equal(
            PluginRegistryPayloads.List(packages),
            PluginRegistryPayloads.List(packages, null, null));
        Assert.DoesNotContain("retired", PluginRegistryPayloads.List(packages, null, []));

        var json = PluginRegistryPayloads.List(packages, null, [new RetiredPackage { Id = "Marketing", Reason = "gone", RetiredAt = "2026-09-08" }]);
        var parsed = PluginRegistryPayloads.ParseList(json);
        var retired = Assert.Single(parsed.Retired);
        Assert.Equal(("Marketing", "gone", "2026-09-08"), (retired.Id, retired.Reason, retired.RetiredAt));
        Assert.Empty(PluginRegistryPayloads.ParseList(PluginRegistryPayloads.List(packages)).Retired);
    }

    [Fact]
    public void TheDeclarationFile_IsParsedStrictly_AndAMalformedOneDeclaresNothing()
    {
        var parsed = NodeRepoPackageSource.ParseRetired(
            """{"retired":[{"id":"Marketing","reason":"gone","retiredAt":"2026-09-08"},{"reason":"no id"},{"id":"a/b"},{"id":"Marketing"}]}""");
        var one = Assert.Single(parsed);
        Assert.Equal("Marketing", one.Id);
        Assert.Empty(NodeRepoPackageSource.ParseRetired(null));
        Assert.Empty(NodeRepoPackageSource.ParseRetired("not json"));
        Assert.Empty(NodeRepoPackageSource.ParseRetired("""{"retired":"Marketing"}"""));
    }

    [Fact]
    public void TheLedger_DeclaresPerSource_ReplacesIt_AndSpeaksOncePerPartition()
    {
        var ledger = new RetiredPackageLedger();
        Assert.Null(ledger.For("Marketing"));
        ledger.Declare("Plugins", [new RetiredPackageDeclaration("Plugins", "Marketing", "gone")]);
        Assert.Equal("gone", ledger.For("marketing")?.Reason);
        Assert.Null(ledger.For("Store"));

        Assert.True(ledger.FirstSighting("Marketing"));
        Assert.False(ledger.FirstSighting("Marketing"));

        // The source stops declaring it (re-introduced): it is no longer retired.
        ledger.Declare("Plugins", []);
        Assert.Null(ledger.For("Marketing"));
    }
}

/// <summary>The removal against a real mesh: the install record goes, the partition's data stays,
/// and a package that is merely absent from a listing is never touched.</summary>
public class ARegistryRetiredPackageIsConvergedAwayTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Retired = "RetiredByRegistry";
    private const string Kept = "StaysInstalled";

    private readonly string moduleRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mw-8619-" + Guid.NewGuid().ToString("N")[..8]);
    private ModuleLandingService? landing;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddPluginCatalog()
        .ConfigureServices(services => services
            .AddSingleton(_ => landing ??= new ModuleLandingService(baseDirectory: EnsureDirectory(moduleRoot)))
            .AddSingleton<IModuleActivationRestart>(new NoRestart()));

    private static string EnsureDirectory(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    private sealed class NoRestart : IModuleActivationRestart
    {
        public IObservable<ModuleRestartOutcome> RequestRestart(string reason) =>
            Observable.Return(new ModuleRestartOutcome(ModuleRestartKinds.Restarted, "counted by the test"));
    }

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private ILogger Log => Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<ARegistryRetiredPackageIsConvergedAwayTest>()
                           ?? NullLogger<ARegistryRetiredPackageIsConvergedAwayTest>.Instance;

    private async Task Installed(string id, CancellationToken ct)
    {
        foreach (var node in new[]
                 {
                     MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{id}") with
                     {
                         NodeType = PackageInstaller.PackageNodeType,
                         Name = id,
                         State = MeshNodeState.Active,
                         Content = new PackageManifest
                         {
                             Id = id, Name = id, Source = "Plugins", Version = "1.0.0", TargetPartition = id,
                             InstalledAtUtc = new DateTimeOffset(2026, 9, 7, 7, 0, 0, TimeSpan.Zero),
                         },
                     },
                     MeshNode.FromPath(id) with { NodeType = "Markdown", Name = id, State = MeshNodeState.Active },
                     MeshNode.FromPath($"{id}/Guide") with { NodeType = "Markdown", Name = "Guide", State = MeshNodeState.Active },
                 })
            await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(node)).Timeout(TestTimeouts.Convergence).Await(ct);
    }

    private Task<bool> Exists(string path, CancellationToken ct) =>
        Storage.Read(path, Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .Select(n => n is not null).Timeout(TestTimeouts.Convergence).Await(ct);

    private static RegistryListing Listing(string[] listed, params RetiredPackage[] retired) =>
        new() { Packages = [.. listed.Select(id => new PackageManifest { Id = id, Name = id, Source = "Plugins" })], Retired = [.. retired] };

    /// <summary>The arm under test: the registry's listing carries the package in its retired set → its
    /// install record is removed through phase 1, the partition's data stays, the package that is
    /// still listed is untouched, and the compile control plane is told.</summary>
    [Fact(Timeout = 240_000)]
    public async Task ARetiredInTheListingPackage_LosesItsInstallRecord_AndKeepsItsData()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed(Retired, ct);
        await Installed(Kept, ct);
        var listing = Listing([Kept], new RetiredPackage { Id = Retired, Reason = "retired from the repository" });

        var proven = RetiredPackageConvergence.Declare(Mesh, "Plugins", listing, Log);
        var outcome = await RetiredPackageConvergence.Retire(Mesh, proven, Log).Timeout(TestTimeouts.Convergence).Await(ct);

        Assert.Single(outcome.Requested);
        await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(outcome.Requested[0]))
            .Select(n => n.ContentAs<PackageUninstallRequest>(Mesh.JsonSerializerOptions))
            .OfType<PackageUninstallRequest>()
            .Where(r => r.Status == PackageUninstallStatus.AwaitingConfirmation || PackageUninstallStatus.IsTerminal(r.Status))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        Assert.False(await Exists($"{PackageInstaller.InstalledPartition}/{Retired}", ct), "the install record is removed");
        Assert.True(await Exists($"{Retired}/Guide", ct), "the partition's data stays");
        Assert.True(await Exists($"{PackageInstaller.InstalledPartition}/{Kept}", ct), "a still-listed package is untouched");
        Assert.Equal(Retired, Mesh.ServiceProvider.GetRequiredService<IRetiredPackages>().For(Retired)?.PackageId);
    }

    /// <summary>The NEGATIVE control: the package is merely absent from a (partial) listing and the
    /// registry declares nothing retired. Nothing is proven, so no request is written and the record
    /// stays — an outage or a partial listing must never uninstall anything.</summary>
    [Fact(Timeout = 240_000)]
    public async Task APackageAbsentFromAPartialListing_IsNeverRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed(Retired, ct);
        await Installed(Kept, ct);
        var partial = Listing([Kept]);

        var proven = RetiredPackageConvergence.Declare(Mesh, "Plugins", partial, Log);
        var outcome = await RetiredPackageConvergence.Retire(Mesh, proven, Log).Timeout(TestTimeouts.Convergence).Await(ct);

        Assert.Empty(proven);
        Assert.Empty(outcome.Requested);
        Assert.True(await Exists($"{PackageInstaller.InstalledPartition}/{Retired}", ct), "absence proves nothing");
        Assert.True(await Exists($"{PackageInstaller.InstalledPartition}/{Kept}", ct));
        Assert.Null(Mesh.ServiceProvider.GetRequiredService<IRetiredPackages>().For(Retired));
    }
}
