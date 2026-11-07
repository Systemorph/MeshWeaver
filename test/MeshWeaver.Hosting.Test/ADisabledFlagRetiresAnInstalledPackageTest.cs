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
using MeshWeaver.Plugin.Packaging;
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
    public void TheRetireKey_IsTheSameForTheSameInstall_AndNewForANewOne()
    {
        var at = new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.Zero);
        var first = Record("Mcp") with { InstalledAtUtc = at };
        Assert.Equal(DeclaredDisabledRetirement.RetireKeyFor(first), DeclaredDisabledRetirement.RetireKeyFor(first with { }));
        Assert.NotEqual(DeclaredDisabledRetirement.RetireKeyFor(first),
            DeclaredDisabledRetirement.RetireKeyFor(first with { InstalledAtUtc = at.AddMinutes(5) }));
        Assert.NotEqual(DeclaredDisabledRetirement.RequestIdFor("k", 1), DeclaredDisabledRetirement.RequestIdFor("k", 2));
    }

    /// <summary>#7959 review: ids that SANITIZE alike must not share a key. Negative control: the
    /// sanitized segment alone — what the key used to be — IS equal for them.</summary>
    [Fact]
    public void IdsThatSanitizeAlike_GetDifferentKeys()
    {
        var a = Record("Mcp.Host");
        var b = Record("Mcp-Host");
        Assert.Equal(MeshWeaver.Graph.Configuration.ActivationRecycle.Segment("flag-retire-Mcp.Host-0-1.0.0"),
            MeshWeaver.Graph.Configuration.ActivationRecycle.Segment("flag-retire-Mcp-Host-0-1.0.0"));
        Assert.NotEqual(DeclaredDisabledRetirement.RetireKeyFor(a), DeclaredDisabledRetirement.RetireKeyFor(b));
        // Legacy records with no install time and no version too.
        Assert.NotEqual(DeclaredDisabledRetirement.RetireKeyFor(a with { Version = null }),
            DeclaredDisabledRetirement.RetireKeyFor(b with { Version = null }));
    }
}

/// <summary>The retire against a real mesh: the request, its executor, the install records and the content.</summary>
public class ADisabledFlagRetiresAnInstalledPackageTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Package = "FlagRetirePkg";
    private const string Dependent = "FlagRetireDependent";

    private readonly string moduleRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mw-5055-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CountingRestart restart = new();
    private ModuleLandingService? landing;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddPluginCatalog()
        .ConfigureServices(services => services
            // Registered AFTER the catalog's own, so these are the instances resolved.
            .AddSingleton(_ => landing ??= new ModuleLandingService(baseDirectory: EnsureDirectory(moduleRoot)))
            .AddSingleton<IModuleActivationRestart>(restart));

    private static string EnsureDirectory(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Counts restart requests — the deployment roll this test must see at most once.</summary>
    private sealed class CountingRestart : IModuleActivationRestart
    {
        private int count;
        public int Count => Volatile.Read(ref count);

        public IObservable<ModuleRestartOutcome> RequestRestart(string reason) => Observable.Defer(() =>
        {
            Interlocked.Increment(ref count);
            return Observable.Return(new ModuleRestartOutcome(ModuleRestartKinds.Restarted, "counted by the test"));
        });
    }

    private ModuleLandingService Landing => Mesh.ServiceProvider.GetRequiredService<ModuleLandingService>();

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

    private Task Installed(string id, CancellationToken ct, params string[] requires) =>
        Installed(id, ct, partition: id, module: null, requires);

    private async Task Installed(string id, CancellationToken ct, string partition, string? module, params string[] requires)
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
                             Id = id, Name = id, Source = "Plugins", Version = "1.0.0", TargetPartition = partition, Module = module,
                             Requires = [.. requires],
                             InstalledAtUtc = new DateTimeOffset(2026, 11, 7, 7, 0, 0, TimeSpan.Zero),
                         },
                     },
                     MeshNode.FromPath(partition) with { NodeType = "Markdown", Name = partition, State = MeshNodeState.Active },
                     MeshNode.FromPath($"{partition}/Guide") with { NodeType = "Markdown", Name = "Guide", State = MeshNodeState.Active },
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

    private Task<PackageUninstallRequest> Read(string path, Func<PackageUninstallRequest, bool> until, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path))
            .Select(n => n.ContentAs<PackageUninstallRequest>(Mesh.JsonSerializerOptions))
            .OfType<PackageUninstallRequest>()
            .Where(until)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>
    /// #7959 review (dependency retired while its dependent's retire is refused): Essentials-like
    /// <c>Dependent</c> requires <c>Package</c>, both are declared off, and the dependent's phase 1 is
    /// REFUSED (its partition is shared with a package that stays). The dependency must NOT be
    /// retired, and the refusal is reported on THIS boot. Negative control (measured): without the
    /// re-validation after each settlement the dependency is retired and this test is red.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ADependentRefusedByPhaseOne_KeepsWhatItRequires_AndTheRefusalIsReportedThisBoot()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed(Package, ct);
        await Installed(Dependent, ct, partition: Dependent, module: null, $"{Package}@^1.0");
        await Installed("FlagRetireSharer", ct, partition: Dependent, module: null);

        var outcome = await Retire(Declared("off", enabled: false, $"Plugins/{Package}", $"Plugins/{Dependent}"), ct);

        Assert.Contains(outcome.Refused, line => line.StartsWith(Dependent + ":") && line.Contains("shared"));
        Assert.Contains(outcome.Refused, line => line.StartsWith(Package + ":") && line.Contains(Dependent));
        Assert.True(await Exists($"{PackageInstaller.InstalledPartition}/{Dependent}", ct), "the refused dependent stays");
        Assert.True(await Exists($"{PackageInstaller.InstalledPartition}/{Package}", ct), "and so does what it requires");
    }

    /// <summary>
    /// #7959 review (a failed request blocked every later attempt): attempt 1 is refused (shared
    /// partition); once the cause is gone the next boot re-evaluates with attempt 2, which retires.
    /// Negative control: the first boot's refusal — the same install, the same declaration.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AFailedAttempt_IsReevaluatedOnTheNextBoot_WithOneActiveAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed(Package, ct);
        await Installed("FlagRetireSharer", ct, partition: Package, module: null);
        var composition = Declared("off", enabled: false, $"Plugins/{Package}");

        var first = await Retire(composition, ct);
        var attempt1 = Assert.Single(first.Requested);
        Assert.Equal(PackageUninstallStatus.Failed, (await Settled(attempt1, ct)).Status);
        Assert.Contains(first.Refused, line => line.StartsWith(Package + ":"));
        Assert.True(await Exists($"{PackageInstaller.InstalledPartition}/{Package}", ct));

        await Access.RunAsSystem(() => NodeFactory.DeleteNode($"{PackageInstaller.InstalledPartition}/FlagRetireSharer"))
            .Timeout(TestTimeouts.Convergence).Await(ct);

        var second = await Retire(composition, ct);
        var attempt2 = Assert.Single(second.Requested);
        Assert.NotEqual(attempt1, attempt2);
        Assert.EndsWith("-a2", attempt2);
        Assert.Equal(PackageUninstallStatus.AwaitingConfirmation, (await Settled(attempt2, ct)).Status);
        Assert.False(await Exists($"{PackageInstaller.InstalledPartition}/{Package}", ct), "retired on re-evaluation");
    }

    /// <summary>
    /// #7959 review (a real landed module): the activation entry is disabled DURABLY, and exactly ONE
    /// restart is asked for — after phase 1 is persisted — and never again on a later boot.
    /// Negative control: before the boot retire asks, no restart has happened (phase 1 alone never rolls).
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ALandedModule_IsDisabledDurably_AndExactlyOneRestartRollsEveryReplica()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "MeshWeaver.FlagRetireProbe";
        await Landing.LandModule(module, [(module + ".dll", System.IO.File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
                packagePath: $"{PackageInstaller.InstalledPartition}/{Package}", version: "1.0.0")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.Contains((await Landing.GetActivation().Take(1).Timeout(TestTimeouts.Convergence).Await(ct)).Entries,
            e => e.Enabled && e.Name == module);
        await Installed(Package, ct, partition: Package, module: module);
        var composition = Declared("off", enabled: false, $"Plugins/{Package}");

        var outcome = await Retire(composition, ct);
        var path = Assert.Single(outcome.Requested);
        var restarted = await Read(path, r => r.ModuleOutcome?.Contains("one restart requested") == true, ct);

        Assert.True(restarted.ModuleDisabled, restarted.ModuleOutcome ?? "");
        Assert.Equal(path, restarted.RestartCoveredBy);
        Assert.Contains("one restart requested", restarted.ModuleOutcome ?? "");
        Assert.DoesNotContain((await Landing.GetActivation().Take(1).Timeout(TestTimeouts.Convergence).Await(ct)).Entries,
            e => e.Enabled && e.Name == module);
        Assert.Equal(1, restart.Count);

        // The next boot (every replica, after the roll): nothing is decided, nothing restarts again.
        var again = await Retire(composition, ct);
        Assert.Empty(again.Requested);
        Assert.Equal(1, restart.Count);
    }

    /// <summary>
    /// #7959 review (restart after a failed durable disable): a module LOADED here that the store lane
    /// never landed cannot be disabled durably, so the flag retire FAILS — install record kept, no
    /// restart. Negative control: <see cref="ALandedModule_IsDisabledDurably_AndExactlyOneRestartRollsEveryReplica"/>.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AModuleThatCannotBeDisabledDurably_FailsTheRetire_AndRestartsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed(Package, ct, partition: Package, module: "MeshWeaver.PluginCatalog");

        var outcome = await Retire(Declared("off", enabled: false, $"Plugins/{Package}"), ct);
        var request = await Settled(Assert.Single(outcome.Requested), ct);

        Assert.Equal(PackageUninstallStatus.Failed, request.Status);
        Assert.Contains("not landed by the store lane", request.Failure ?? "");
        Assert.Contains(outcome.Refused, line => line.StartsWith(Package + ":"));
        Assert.True(await Exists($"{PackageInstaller.InstalledPartition}/{Package}", ct), "the install record is kept");
        Assert.Equal(0, restart.Count);
    }

    /// <summary>#7959 review: a request with no package is REFUSED by validation, never an NRE.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ARequestWithNoPackage_IsRefusedByValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        var ticket = await PackageUninstall.Request(Mesh, new PackageUninstallRequest { Package = null!, Reason = "x" })
            .Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.False(ticket.Accepted);
        Assert.Contains("needs a package id", ticket.Refusal);
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

    /// <summary>
    /// The UNATTENDED UPDATE lane over a pinned install record with a newer candidate: it reaches the
    /// policy decision ("has an update") unless the declaration retires the package first. The
    /// record is pinned (<see cref="PackageUpdatePolicy.None"/>) so the decision writes nothing.
    /// Returns what the lane logged.
    /// </summary>
    protected async Task<IReadOnlyList<string>> UnattendedUpdate(CancellationToken ct)
    {
        var record = new PackageManifest
        {
            Id = Package, Name = Package, Source = "Plugins", Version = "1.0.0", TargetPartition = Package,
            ModuleVersion = "v1", UpdatePolicy = PackageUpdatePolicy.None,
        };
        await Mesh.ServiceProvider.GetRequiredService<AccessService>().RunAsSystem(() => NodeFactory.CreateOrUpdateNode(
                MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{Package}") with
                {
                    NodeType = PackageInstaller.PackageNodeType, Name = Package, State = MeshNodeState.Active, Content = record,
                }))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var log = new CapturingLogger();
        await PackageUpdateReconciler.ReconcileInstalled(Mesh, new SilentSource(), "HEAD",
                [record with { ModuleVersion = "v2" }], "test", log)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        return log.Lines;
    }

    private sealed class SilentSource : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return((IReadOnlyList<PackageManifest>)[]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            Observable.Return((IReadOnlyList<PackageFile>)[]);
    }

    private sealed class CapturingLogger : ILogger
    {
        private ImmutableList<string> lines = ImmutableList<string>.Empty;
        public IReadOnlyList<string> Lines => lines;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            ImmutableInterlocked.Update(ref lines, l => l.Add(formatter(state, exception)));
    }

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

    /// <summary>#7959 review: the unattended UPDATE lane never touches a package the declaration retires.
    /// Negative control: <see cref="AReassertWithNoDeclaredOffFlagTest.AnUnattendedUpdate_ReachesItsDecision_WithNoFlag"/>.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AnUnattendedUpdate_SkipsADeclaredOffPackage()
    {
        var lines = await UnattendedUpdate(TestContext.Current.CancellationToken);
        Assert.Contains(lines, l => l.Contains("declared off by the feature flag 'off'"));
        Assert.DoesNotContain(lines, l => l.Contains("has an update"));
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

    [Fact(Timeout = 120_000)]
    public async Task AnUnattendedUpdate_ReachesItsDecision_WithNoFlag()
    {
        var lines = await UnattendedUpdate(TestContext.Current.CancellationToken);
        Assert.Contains(lines, l => l.Contains("has an update"));
        Assert.DoesNotContain(lines, l => l.Contains("declared off by the feature flag"));
    }
}
