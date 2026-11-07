using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Features;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A flag DECLARED off retires what is installed; a flag merely absent retires nothing</b>
/// (MeshWeaver.Plugins#5055). The estate's case: its record says <c>Features__Flags__mcp__Enabled=false</c>,
/// and the already-installed package must go — through phase 1 of the one uninstall request, keeping
/// its data. The composition rule is pinned purely; the retire runs against a real mesh with the
/// plugin catalog's request executor.
/// </summary>
public class DeclaredDisabledFlagCompositionTest
{
    /// <summary>The reader's current composition — its flags are a BehaviorSubject, so the replay
    /// arrives inside Subscribe; no blocking bridge.</summary>
    internal static FeatureComposition Composed(IFeatureFlags flags)
    {
        FeatureComposition? composition = null;
        using (flags.Composition.Take(1).Subscribe(c => composition = c)) { }
        return composition ?? throw new InvalidOperationException("the flag reader replayed no composition");
    }

    private static FeatureComposition Compose(Dictionary<string, string?> values)
    {
        using var flags = new ConfigurationFeatureFlags(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return Composed(flags);
    }

    [Fact]
    public void AnExplicitFalse_RetiresItsPackages()
    {
        var composition = Compose(new()
        {
            ["Features:Flags:mcp:Enabled"] = "false",
            ["Features:Flags:mcp:Packages:0"] = "Plugins/Mcp",
        });
        Assert.Equal(["Plugins/Mcp"], composition.Excluded.Select(p => p.Package));
        Assert.Equal(["Plugins/Mcp"], composition.Retired.Select(p => p.Package));
        Assert.Equal("mcp", composition.Retired.Single().Flag);
    }

    /// <summary>Negative control: a value that is not a boolean is not consent to INSTALL, and it is
    /// not a declaration either — it excludes, and it retires nothing.</summary>
    [Fact]
    public void ANonBooleanEnabled_ExcludesButDoesNotRetire()
    {
        var composition = Compose(new()
        {
            ["Features:Flags:mcp:Enabled"] = "no",
            ["Features:Flags:mcp:Packages:0"] = "Plugins/Mcp",
        });
        Assert.Equal(["Plugins/Mcp"], composition.Excluded.Select(p => p.Package));
        Assert.Empty(composition.Retired);
    }

    /// <summary>Negative control: an ABSENT flag (or one declared on) is never a removal.</summary>
    [Fact]
    public void AnAbsentOrEnabledFlag_RetiresNothing()
    {
        Assert.Empty(Compose(new()).Retired);
        Assert.Empty(Compose(new() { ["Features:Flags:mcp:Packages:0"] = "Plugins/Mcp" }).Retired);
        Assert.Empty(Compose(new() { ["Features:Flags:mcp:Enabled"] = "true", ["Features:Flags:mcp:Packages:0"] = "Plugins/Mcp" }).Retired);
    }

    private static PackageManifest Record(string id, params string[] requires) =>
        new() { Id = id, Name = id, Source = "Plugins", TargetPartition = id, Version = "1.0.0", Requires = [.. requires] };

    [Fact]
    public void APackageAnotherInstalledPackageRequires_IsRefusedByName()
    {
        var installed = new[] { Record("Mcp", "AI@^1"), Record("Essentials", "Mcp@^1.0"), Record("AI") };
        var decision = DeclaredDisabledRetirement.Decide(installed, [new FeaturePackage("mcp", "Plugins/Mcp")]);

        Assert.Empty(decision.Retire);
        var refused = Assert.Single(decision.RefusedRequired);
        Assert.Equal("Mcp", refused.Record.Id);
        Assert.Equal(["Essentials"], refused.RequiredBy);
    }

    [Fact]
    public void DeclaringTheDependentOffToo_RetiresBoth()
    {
        var installed = new[] { Record("Mcp", "AI@^1"), Record("Essentials", "Mcp@^1.0"), Record("AI") };
        var decision = DeclaredDisabledRetirement.Decide(installed,
            [new FeaturePackage("mcp", "Plugins/Mcp"), new FeaturePackage("mcp", "Plugins/Essentials")]);

        Assert.Equal(["Essentials", "Mcp"], decision.Retire.Select(c => c.Record.Id).Order(StringComparer.Ordinal));
        Assert.Empty(decision.RefusedRequired);
    }

    [Fact]
    public void ARefusalPropagates_ToWhatTheRefusedPackageRequires()
    {
        // Codex requires Mcp; Essentials (staying) requires Codex. Codex is refused, so it stays
        // installed — and Mcp, which it requires, must be refused with it.
        var installed = new[] { Record("Mcp"), Record("Codex", "Mcp"), Record("Essentials", "Codex") };
        var decision = DeclaredDisabledRetirement.Decide(installed,
            [new FeaturePackage("off", "Plugins/Mcp"), new FeaturePackage("off", "Plugins/Codex")]);

        Assert.Empty(decision.Retire);
        Assert.Equal(["Codex", "Mcp"], decision.RefusedRequired.Select(r => r.Record.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ARecordNamingNoSource_IsNotRetired_ButSaid()
    {
        var legacy = Record("Mcp") with { Source = null };
        var decision = DeclaredDisabledRetirement.Decide([legacy], [new FeaturePackage("mcp", "Plugins/Mcp")]);
        Assert.Empty(decision.Retire);
        Assert.Equal("Mcp", Assert.Single(decision.NoSource).Record.Id);
    }

    [Fact]
    public void TheRequestId_IsTheSameForTheSameInstall_AndNewForANewOne()
    {
        var at = new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.Zero);
        var first = Record("Mcp") with { InstalledAtUtc = at };
        Assert.Equal(DeclaredDisabledRetirement.RequestIdFor(first), DeclaredDisabledRetirement.RequestIdFor(first with { }));
        Assert.NotEqual(DeclaredDisabledRetirement.RequestIdFor(first),
            DeclaredDisabledRetirement.RequestIdFor(first with { InstalledAtUtc = at.AddMinutes(5) }));
    }
}

/// <summary>The retire against a real mesh: the request, its executor, the install records and the content.</summary>
public class ADisabledFlagRetiresAnInstalledPackageTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Package = "FlagRetirePkg";
    private const string Dependent = "FlagRetireDependent";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder).AddPluginCatalog();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
    private ILogger Log => Mesh.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<ADisabledFlagRetiresAnInstalledPackageTest>()
                           ?? NullLogger<ADisabledFlagRetiresAnInstalledPackageTest>.Instance;

    private static FeatureComposition Declared(string flag, bool? enabled, params string[] packages)
    {
        var values = new Dictionary<string, string?>();
        if (enabled is { } e)
            values[$"Features:Flags:{flag}:Enabled"] = e ? "true" : "false";
        for (var i = 0; i < packages.Length; i++)
            values[$"Features:Flags:{flag}:Packages:{i}"] = packages[i];
        using var flags = new ConfigurationFeatureFlags(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return DeclaredDisabledFlagCompositionTest.Composed(flags);
    }

    private async Task Installed(string id, CancellationToken ct, params string[] requires)
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
                             Requires = [.. requires],
                             InstalledAtUtc = new DateTimeOffset(2026, 11, 7, 7, 0, 0, TimeSpan.Zero),
                         },
                     },
                     MeshNode.FromPath(id) with { NodeType = "Markdown", Name = id, State = MeshNodeState.Active },
                     MeshNode.FromPath($"{id}/Guide") with { NodeType = "Markdown", Name = "Guide", State = MeshNodeState.Active },
                 })
            await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(node)).Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.True(await Exists($"{PackageInstaller.InstalledPartition}/{id}", ct), "the premise: the package is installed");
    }

    private Task<bool> Exists(string path, CancellationToken ct) =>
        Storage.Read(path, Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .Select(n => n is not null).Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<DeclaredDisabledRetirement.Outcome> Retire(FeatureComposition composition, CancellationToken ct) =>
        DeclaredDisabledRetirement.Retire(Mesh, composition, Log).Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<PackageUninstallRequest> Settled(string path, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path))
            .Select(n => n.ContentAs<PackageUninstallRequest>(Mesh.JsonSerializerOptions))
            .OfType<PackageUninstallRequest>()
            .Where(r => r.Status == PackageUninstallStatus.AwaitingConfirmation || PackageUninstallStatus.IsTerminal(r.Status))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<int> Requests(CancellationToken ct) =>
        Access.RunAsSystem(() => NodeFactory.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"namespace:{PackageUninstallRequest.Namespace} scope:children nodeType:{PackageUninstallRequest.NodeType}")).Take(1))
            .Select(change => change.Items.Count)
            .Timeout(TestTimeouts.Convergence).Await(ct);

    [Fact(Timeout = 240_000)]
    public async Task AFlagDeclaredOff_RetiresTheInstalledPackage_KeepsItsData_AndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed(Package, ct);
        var composition = Declared("off", enabled: false, $"Plugins/{Package}");

        var outcome = await Retire(composition, ct);
        var path = Assert.Single(outcome.Requested);
        var request = await Settled(path, ct);

        Assert.True(request.Status == PackageUninstallStatus.AwaitingConfirmation, request.Failure ?? request.Status);
        Assert.Equal("off", request.RetiredByFlag);
        Assert.True(request.RetireOnEveryReplica);
        Assert.Null(request.RequestedBy);
        Assert.False(await Exists($"{PackageInstaller.InstalledPartition}/{Package}", ct), "phase 1 removed the install record");
        Assert.True(await Exists($"{Package}/Guide", ct), "no data is dropped");
        Assert.DoesNotContain(Package,
            await PackageUninstallExecutor.UninstalledHere(Mesh).Timeout(TestTimeouts.Convergence).Await(ct));

        // The next boot (any replica): the record is gone, so nothing is decided and nothing written.
        var again = await Retire(composition, ct);
        Assert.Empty(again.Requested);
        Assert.Equal(1, await Requests(ct));
        Assert.True(await Exists($"{Package}/Guide", ct));
    }

    [Fact(Timeout = 240_000)]
    public async Task TwoReplicasBootingTogether_WriteOneRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed(Package, ct);
        var composition = Declared("off", enabled: false, $"Plugins/{Package}");

        // Both "replicas" decided before either wrote: the derived id collides, the second reads it.
        var outcomes = await Observable.Merge(
                DeclaredDisabledRetirement.Retire(Mesh, composition, Log),
                DeclaredDisabledRetirement.Retire(Mesh, composition, Log))
            .ToList().Timeout(TestTimeouts.Convergence).Await(ct);

        Assert.Equal(1, outcomes.Sum(o => o.Requested.Count));
        Assert.Equal(1, await Requests(ct));
    }

    /// <summary>Negative control: the same installed package, the flag merely ABSENT — nothing is retired.</summary>
    [Fact(Timeout = 240_000)]
    public async Task AnAbsentFlag_RetiresNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed(Package, ct);

        var outcome = await Retire(Declared("off", enabled: null), ct);

        Assert.Empty(outcome.Requested);
        Assert.Equal(0, await Requests(ct));
        Assert.True(await Exists($"{PackageInstaller.InstalledPartition}/{Package}", ct), "still installed");
    }

    [Fact(Timeout = 240_000)]
    public async Task APackageAnotherInstalledPackageRequires_IsRefusedByName_AndStays()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed(Package, ct);
        await Installed(Dependent, ct, $"{Package}@^1.0");

        var outcome = await Retire(Declared("off", enabled: false, $"Plugins/{Package}"), ct);

        Assert.Empty(outcome.Requested);
        Assert.Contains(outcome.Refused, line => line.Contains(Package) && line.Contains(Dependent));
        Assert.Equal(0, await Requests(ct));
        Assert.True(await Exists($"{PackageInstaller.InstalledPartition}/{Package}", ct), "refused, so still installed");
    }
}

/// <summary>
/// 🚨 <b>The repair's re-assert never re-writes a package a flag declared off</b> (MeshWeaver.Plugins#5055).
/// <see cref="InstalledPackageRepairService"/> lists the install records BEFORE it waits for the
/// default install, so the record it hands to <c>ReassertInstalled</c> can belong to a package the
/// boot pass is retiring at that moment; re-asserting it would re-write the record phase 1 removes.
/// The re-assert reads the same composition and skips by the retire's own rule
/// (<see cref="DeclaredDisabledRetirement.CoveredBy"/>), reporting each skip on the summary.
/// </summary>
public abstract class AReassertSkipsDeclaredOffPackagesTestBase(ITestOutputHelper output, Dictionary<string, string?> flags)
    : MonolithMeshTestBase(output)
{
    protected const string Package = "FlagRetirePkg";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddPluginCatalog()
        .ConfigureServices(services => services.AddSingleton<IFeatureFlags>(
            new ConfigurationFeatureFlags(new ConfigurationBuilder().AddInMemoryCollection(flags).Build())));

    protected Task<DefaultInstallSummary> Reassert(CancellationToken ct, string? source = "Plugins") =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>()
            .ReassertInstalled([new InstanceAutoRegistrationService.ReassertTarget(Package, Package, "1.0.0", source)])
            .Timeout(TestTimeouts.Convergence).Await(ct);
}

public class AReassertSkipsADeclaredOffPackageTest(ITestOutputHelper output)
    : AReassertSkipsDeclaredOffPackagesTestBase(output, new()
    {
        ["Features:Flags:off:Enabled"] = "false",
        ["Features:Flags:off:Packages:0"] = $"Plugins/{Package}",
    })
{
    [Fact(Timeout = 120_000)]
    public async Task APackageAFlagDeclaresOff_IsNotReasserted_AndTheSkipNamesTheFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var summary = await Reassert(ct);
        var skip = Assert.Single(summary.Skipped);
        Assert.Equal(Package, skip.Package);
        Assert.Contains("'off'", skip.Reason);
    }

    /// <summary>The same rule as the retire: a record naming no source is not covered, so it is not skipped.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ARecordNamingNoSource_IsNotSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Empty((await Reassert(ct, source: null)).Skipped);
    }
}

/// <summary>Negative control: the same re-assert with the flag merely ABSENT skips nothing.</summary>
public class AReassertWithNoDeclaredOffFlagTest(ITestOutputHelper output)
    : AReassertSkipsDeclaredOffPackagesTestBase(output, new())
{
    [Fact(Timeout = 120_000)]
    public async Task AnAbsentFlag_SkipsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Empty((await Reassert(ct)).Skipped);
    }
}
