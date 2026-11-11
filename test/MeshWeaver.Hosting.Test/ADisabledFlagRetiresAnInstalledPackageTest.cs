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

    [Fact]
    public void TheLatestFailedAttempt_IsTheHighestAttemptNumber_NotTheLastListed()
    {
        var r = new PackageUninstallRequest { Package = "X" };
        var failed = new List<(string, PackageUninstallRequest)>
        {
            ("Admin/_PackageUninstall/flag-retire-X-k-a10", r),
            ("Admin/_PackageUninstall/flag-retire-X-k-a9", r),
            ("Admin/_PackageUninstall/flag-retire-X-k-a100", r),
            ("Admin/_PackageUninstall/flag-retire-X-k-a11", r),
        };
        Assert.True(DeclaredDisabledRetirement.LatestFailed(failed) is { Path: "Admin/_PackageUninstall/flag-retire-X-k-a100" },
            "the highest attempt number is the superseded one, whatever the listing order");
        Assert.Null(DeclaredDisabledRetirement.LatestFailed([]));
        Assert.Equal(0, DeclaredDisabledRetirement.AttemptNumberOf("Admin/_PackageUninstall/no-attempt"));
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

    /// <summary>A listed install record that yields no manifest id could be a dependent of the package
    /// declared off: dropping it from the dependency picture would retire what it requires. The read
    /// faults instead, so nothing is retired (#7959 review). Negative control: with the record dropped
    /// silently (the old filter), the package is retired and this test goes red.</summary>
    [Fact(Timeout = 240_000)]
    public async Task AnInstallRecordWithNoManifestId_RetiresNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed(Package, ct);
        await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(
                MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{Dependent}") with
                {
                    NodeType = PackageInstaller.PackageNodeType,
                    Name = Dependent,
                    State = MeshNodeState.Active,
                    Content = new PackageManifest { Name = Dependent, Requires = [Package] },
                }))
            .Timeout(TestTimeouts.Convergence).Await(ct);

        var outcome = await Retire(Declared("off", enabled: false, $"Plugins/{Package}"), ct);

        Assert.Empty(outcome.Requested);
        Assert.Equal(0, await Requests(ct));
        Assert.True(await Exists($"{PackageInstaller.InstalledPartition}/{Package}", ct), "the install record is kept");
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
    /// #8020: a refusal that stands (here a shared partition; on memex-cloud the user-data census) is
    /// re-evaluated by every boot, and each boot reports ONLY the attempt it supersedes — never the
    /// whole history. Before the fix the third boot logged one "earlier retire … was refused" Error per
    /// earlier attempt (two here; ~100 per boot on memex-cloud, ~5,000 Errors in two days). Negative
    /// control: the second boot, which has exactly one earlier attempt, logs exactly one line too.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AStandingRefusal_ReportsOnlyTheAttemptItSupersedes_EachBoot()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed(Package, ct);
        await Installed("FlagRetireSharer", ct, partition: Package, module: null);
        var composition = Declared("off", enabled: false, $"Plugins/{Package}");

        var paths = new List<string>();
        var superseding = new List<IReadOnlyList<string>>();
        for (var boot = 1; boot <= 3; boot++)
        {
            var log = new CapturingLogger();
            var outcome = await DeclaredDisabledRetirement.Retire(Mesh, composition, log).Timeout(TestTimeouts.Convergence).Await(ct);
            var path = Assert.Single(outcome.Requested);
            Assert.Equal(PackageUninstallStatus.Failed, (await Settled(path, ct)).Status);
            paths.Add(path);
            superseding.Add(log.Errors.Where(l => l.Contains("the earlier retire")).ToList());
        }

        Assert.EndsWith("-a3", paths[2]);
        Assert.Empty(superseding[0]);
        Assert.Contains(paths[0], Assert.Single(superseding[1]));
        var third = Assert.Single(superseding[2]);
        Assert.Contains(paths[1], third);
        Assert.DoesNotContain(paths[0], third);
        Assert.Contains("1 earlier attempt(s)", third);
    }

    /// <summary>The restart coordinator once it satisfies <paramref name="until"/>. It may not exist
    /// yet, so its existence is re-read from storage first — never a point read of an absent node.</summary>
    private async Task<PackageRestartCoordinatorState> Coordinator(Func<PackageRestartCoordinatorState, bool> until, CancellationToken ct) =>
        await (await CoordinatorStream(ct)).Where(until).FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>The restart coordinator's live content, once it exists — existence re-read from
    /// storage first, never a point read of an absent node.</summary>
    private async Task<IObservable<PackageRestartCoordinatorState>> CoordinatorStream(CancellationToken ct)
    {
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => Storage.Read(PackageRestartCoordinator.Path, Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null))
            .Where(n => n is not null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        return Access.RunAsSystem(() => Mesh.GetMeshNodeStream(PackageRestartCoordinator.Path))
            .Select(n => n.ContentAs<PackageRestartCoordinatorState>(Mesh.JsonSerializerOptions))
            .OfType<PackageRestartCoordinatorState>();
    }

    /// <summary>Whether no enrolment on <paramref name="s"/> is still pending — THE condition the
    /// withdrawal tests assert, waited on directly.</summary>
    private static bool NoPendingMember(PackageRestartCoordinatorState s) =>
        s.Members is null || s.Members.Values.All(m => m.State != PackageRestartMemberState.Pending);

    /// <summary>
    /// Waits until the coordinator has folded every fact AND holds no pending enrolment, and fails
    /// naming the pending members it last saw. 🚨 Never <c>Facts is null</c> alone: that proxy is
    /// already true BEFORE the withdrawal — once the owner folded the Pending fact, the coordinator
    /// reads Facts null with the member Pending. A writer's Withdraw completes on the owner's ack of
    /// the FACT; the owner folds it afterwards, and a reader's replica can still be at the Pending
    /// fold when the retire returns. See <see cref="FactsFolded_IsSatisfiedByAPendingEnrolment_BeforeItsWithdrawal"/>.
    /// </summary>
    private async Task NoPendingEnrolmentIsLeftBehind(CancellationToken ct) =>
        await (await CoordinatorStream(ct))
            .Select(s => new CoordinatorView(s))
            .Should().Within(TestTimeouts.Convergence)
            .Match(v => v.State.Facts is null && NoPendingMember(v.State), "no pending enrolment is left behind", ct);

    /// <summary>The coordinator as a failure message names it: its unfolded facts and its pending members.</summary>
    private sealed record CoordinatorView(PackageRestartCoordinatorState State)
    {
        public override string ToString() =>
            $"facts [{string.Join(", ", State.Facts?.Keys ?? [])}], pending ["
            + string.Join(", ", (State.Members ?? ImmutableDictionary<string, PackageRestartMember>.Empty)
                .Where(kv => kv.Value.State == PackageRestartMemberState.Pending).Select(kv => kv.Key)) + "]";
    }

    /// <summary>
    /// 🚨 The deterministic pin of the flake in <see cref="AnEnrolmentWhoseRequestIsNeverWritten_IsWithdrawn"/>
    /// (CI run 42785226094): the wait it used, <c>Facts is null</c>, is satisfied by a coordinator
    /// whose enrolment is still PENDING — the state after the owner folded the Pending fact and before
    /// any Withdraw reached the reader. Then the withdrawal itself: once stated, the owner folds the
    /// member away, which only the real condition observes.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task FactsFolded_IsSatisfiedByAPendingEnrolment_BeforeItsWithdrawal()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = PackageUninstall.PathAt("flag-retire-proxy-repro-a1");
        await PackageRestartCoordinator.Pending(Mesh, request, "MeshWeaver.FlagRetireProbe", "the proxy repro", Log)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        var folded = await Coordinator(s => s.Facts is null, ct);
        Assert.Equal(PackageRestartMemberState.Pending, folded.Members?.GetValueOrDefault(request)?.State);

        await PackageRestartCoordinator.Withdraw(Mesh, request, Log).Timeout(TestTimeouts.Convergence).Await(ct);
        await NoPendingEnrolmentIsLeftBehind(ct);
    }

    private Task Landed(string module, string package, CancellationToken ct) =>
        Landing.LandModule(module, [(module + ".dll", System.IO.File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
                packagePath: $"{PackageInstaller.InstalledPartition}/{package}", version: "1.0.0")
            .Timeout(TestTimeouts.Convergence).Await(ct);

    private static bool AllAnswered(PackageRestartCoordinatorState s) =>
        s.Dispatches is { Count: > 0 } d && d.Values.All(x => PackageRestartDispatchState.HasAnswered(x.State))
        && s.Members?.Values.All(m => m.State == PackageRestartMemberState.Covered) == true;

    /// <summary>
    /// #7959 review (restart after a failed durable disable): a LANDED module is disabled durably on
    /// the shared volume, and the restart that makes every replica drop it is the instance's ONE
    /// restart coordinator's — dispatched once, covering the request, although the module is not
    /// loaded in THIS process (a sibling may hold it) — and never again on a later boot.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ALandedModule_IsDisabledDurably_AndExactlyOneRestartRollsEveryReplica()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "MeshWeaver.FlagRetireProbe";
        await Landed(module, Package, ct);
        Assert.Contains((await Landing.GetActivation().Take(1).Timeout(TestTimeouts.Convergence).Await(ct)).Entries,
            e => e.Enabled && e.Name == module);
        await Installed(Package, ct, partition: Package, module: module);
        var composition = Declared("off", enabled: false, $"Plugins/{Package}");

        var outcome = await Retire(composition, ct);
        var path = Assert.Single(outcome.Requested);
        var retired = await Settled(path, ct);

        Assert.Equal(PackageUninstallStatus.AwaitingConfirmation, retired.Status);
        Assert.True(retired.ModuleDisabled, retired.ModuleOutcome ?? "");
        Assert.Contains("restart coordinator", retired.ModuleOutcome ?? "");
        Assert.DoesNotContain((await Landing.GetActivation().Take(1).Timeout(TestTimeouts.Convergence).Await(ct)).Entries,
            e => e.Enabled && e.Name == module);
        var state = await Coordinator(AllAnswered, ct);
        var dispatch = Assert.Single(state.Dispatches!.Values);
        Assert.Equal(PackageRestartDispatchState.Accepted, dispatch.State);
        Assert.Equal([path], dispatch.Covers);
        Assert.Equal(1, restart.Count);

        // The next boot (every replica, after the roll): nothing is decided, nothing restarts again.
        var again = await Retire(composition, ct);
        Assert.Empty(again.Requested);
        Assert.Single((await Coordinator(s => s.Facts is null, ct)).Dispatches!.Values);
        Assert.Equal(1, restart.Count);
    }

    /// <summary>
    /// 🚨 ONE restart for a BATCH of flag retirements (PR C, <see cref="PackageRestartCoordinator"/>):
    /// two packages declared off, each with a landed module, are retired one after the other — and
    /// the instance restarts ONCE, the one dispatch covering both requests. The boot pass enrols
    /// every request it will write as pending BEFORE the first runs, so the first request's ready
    /// cannot dispatch a restart the second would need again. Negative control (measured): with the
    /// boot pass's enrolment removed, the first request dispatches alone and the second dispatches
    /// again — two restarts, two dispatches.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task TwoPackagesDeclaredOff_RestartTheInstanceOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        const string moduleA = "MeshWeaver.FlagRetireProbeA";
        const string moduleB = "MeshWeaver.FlagRetireProbeB";
        const string other = "FlagRetireOther";
        await Landed(moduleA, Package, ct);
        await Landed(moduleB, other, ct);
        await Installed(Package, ct, partition: Package, module: moduleA);
        await Installed(other, ct, partition: other, module: moduleB);

        var outcome = await Retire(Declared("off", enabled: false, $"Plugins/{Package}", $"Plugins/{other}"), ct);

        Assert.Equal(2, outcome.Requested.Count);
        var requests = await Task.WhenAll(outcome.Requested.Select(p => Settled(p, ct)));
        Assert.All(requests, r => Assert.True(r.Status == PackageUninstallStatus.AwaitingConfirmation && r.ModuleDisabled,
            $"{r.Package}: {r.Status} {r.Failure ?? r.ModuleOutcome}"));
        var state = await Coordinator(AllAnswered, ct);
        var dispatch = Assert.Single(state.Dispatches!.Values);
        Assert.Equal(outcome.Requested.Order(StringComparer.Ordinal), dispatch.Covers.Order(StringComparer.Ordinal));
        Assert.Equal(1, restart.Count);
    }

    /// <summary>
    /// An enrolment whose request is never written — the package is required by one that stays —
    /// is WITHDRAWN when the walk ends, so it holds no other restart back for the pending budget.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnEnrolmentWhoseRequestIsNeverWritten_IsWithdrawn()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "MeshWeaver.FlagRetireProbe";
        await Landed(module, Package, ct);
        await Installed(Package, ct, partition: Package, module: module);
        // The dependent is declared off too, but its own phase 1 is refused (it shares its partition
        // with a third package), so it stays — and what it requires is never written.
        await Installed(Dependent, ct, partition: Dependent, module: null, $"{Package}@^1.0");
        await Installed("FlagRetireSharer", ct, partition: Dependent, module: null);

        var outcome = await Retire(Declared("off", enabled: false, $"Plugins/{Package}", $"Plugins/{Dependent}"), ct);

        Assert.Contains(outcome.Refused, line => line.StartsWith(Package + ":"));
        Assert.Contains(Dependent, Assert.Single(outcome.Requested), StringComparison.OrdinalIgnoreCase);
        Assert.True(await Exists($"{PackageInstaller.InstalledPartition}/{Package}", ct), "refused, so still installed");
        await NoPendingEnrolmentIsLeftBehind(ct);
        Assert.Equal(0, restart.Count);
    }

    /// <summary>
    /// #7959 review (restart after a failed durable disable): a module LOADED here that the store lane
    /// never landed cannot be disabled durably, so the flag retire FAILS — install record kept, no
    /// restart — and no pending enrolment is left behind. Negative control: <see cref="ALandedModule_IsDisabledDurably_AndExactlyOneRestartRollsEveryReplica"/>.
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
        // The boot pass enrolled the request; its failed phase 1 withdrew it.
        await NoPendingEnrolmentIsLeftBehind(ct);
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
/// 🚨 <b>No unattended writer re-writes an install a flag is RETIRING — and a REFUSED retire stays
/// repairable and updatable</b> (MeshWeaver.Plugins#5055, #7959 review). The repair lists its records
/// before it waits for the boot pass, and the update lanes can run while phase 1 does (a settle wait
/// that times out, a webhook), so each consults the retirement's actual STATE
/// (<see cref="DeclaredDisabledRetirement.IsRetiring"/>), never the flag match.
/// </summary>
public class AnUnattendedWriterKeepsOffARetiringInstallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Mcp = "FlagRetireMcp";
    private const string Essentials = "FlagRetireEssentials";
    private static readonly DateTimeOffset InstalledAt = new(2026, 11, 7, 7, 0, 0, TimeSpan.Zero);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder).AddPluginCatalog();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static PackageManifest Record(string id, params string[] requires) => new()
    {
        Id = id, Name = id, Source = "Plugins", Version = "1.0.0", TargetPartition = id, InstalledAtUtc = InstalledAt,
        ModuleVersion = "v1", UpdatePolicy = PackageUpdatePolicy.None, Requires = [.. requires],
    };

    /// <summary>Writes the install record as a writer that read it earlier would — the same install identity.</summary>
    private Task Write(PackageManifest record, CancellationToken ct) =>
        Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(
                MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{record.Id}") with
                {
                    NodeType = PackageInstaller.PackageNodeType, Name = record.Id, State = MeshNodeState.Active, Content = record,
                }))
            .Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<DeclaredDisabledRetirement.Outcome> Retire(CancellationToken ct, params string[] packages)
    {
        var values = new Dictionary<string, string?> { ["Features:Flags:off:Enabled"] = "false" };
        for (var i = 0; i < packages.Length; i++)
            values[$"Features:Flags:off:Packages:{i}"] = packages[i];
        using var flags = new ConfigurationFeatureFlags(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return DeclaredDisabledRetirement.Retire(Mesh, DeclaredDisabledFlagCompositionTest.Composed(flags),
                NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);
    }

    private Task<DefaultInstallSummary> Reassert(PackageManifest record, CancellationToken ct) =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>()
            .ReassertInstalled([new InstanceAutoRegistrationService.ReassertTarget(
                record.Id, record.Id, record.ModuleVersion!, record.Source, DeclaredDisabledRetirement.RetireKeyFor(record))])
            .Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>The unattended UPDATE lane over a pinned record with a newer candidate: it reaches its
    /// policy decision ("has an update") unless the install is being retired. Returns what it logged.</summary>
    private async Task<IReadOnlyList<string>> UnattendedUpdate(PackageManifest record, CancellationToken ct)
    {
        var log = new CapturingLogger();
        await PackageUpdateReconciler.ReconcileInstalled(Mesh, new SilentSource(), "HEAD",
                [record with { ModuleVersion = "v2" }], "test", log)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        return log.Lines;
    }

    /// <summary>A retired install whose record a stale writer brings back (it read the record before
    /// phase 1 removed it): the repair skips it, and the update lane does not touch it.</summary>
    [Fact(Timeout = 240_000)]
    public async Task AStaleWriterOfARetiredInstall_IsNeitherReassertedNorUpdated()
    {
        var ct = TestContext.Current.CancellationToken;
        var mcp = Record(Mcp);
        await Write(mcp, ct);
        var outcome = await Retire(ct, $"Plugins/{Mcp}");
        Assert.Single(outcome.Requested);
        Assert.Empty(outcome.Refused);

        await Write(mcp, ct); // the stale writer: same install identity, after phase 1 removed it

        var skip = Assert.Single((await Reassert(mcp, ct)).Skipped);
        Assert.Equal(Mcp, skip.Package);
        var lines = await UnattendedUpdate(mcp, ct);
        Assert.Contains(lines, l => l.Contains("is being retired by a feature flag"));
        Assert.DoesNotContain(lines, l => l.Contains("has an update"));
    }

    /// <summary>
    /// THE regression for the review: <c>Mcp</c> is declared off but <c>Essentials</c> (which stays)
    /// requires it, so the retire is REFUSED — and Mcp stays repairable and updatable. Negative control
    /// (measured): with the skip keyed on the flag match, as before, Mcp is skipped here and this is red.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ARefusedRetire_StaysRepairableAndUpdatable()
    {
        var ct = TestContext.Current.CancellationToken;
        var mcp = Record(Mcp);
        await Write(mcp, ct);
        await Write(Record(Essentials, $"{Mcp}@^1.0"), ct);

        var outcome = await Retire(ct, $"Plugins/{Mcp}");
        Assert.Empty(outcome.Requested);
        Assert.Contains(outcome.Refused, l => l.StartsWith(Mcp + ":") && l.Contains(Essentials));

        Assert.Empty((await Reassert(mcp, ct)).Skipped);
        var lines = await UnattendedUpdate(mcp, ct);
        Assert.Contains(lines, l => l.Contains("has an update"));
        Assert.DoesNotContain(lines, l => l.Contains("is being retired"));
    }

    private sealed class SilentSource : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return((IReadOnlyList<PackageManifest>)[]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            Observable.Return((IReadOnlyList<PackageFile>)[]);
    }
}

/// <summary>Records every formatted line and its level.</summary>
internal sealed class CapturingLogger : ILogger
{
    private ImmutableList<(LogLevel Level, string Line)> entries = ImmutableList<(LogLevel, string)>.Empty;
    public IReadOnlyList<string> Lines => entries.Select(e => e.Line).ToImmutableList();
    public IReadOnlyList<string> Errors => entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Line).ToImmutableList();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        ImmutableInterlocked.Update(ref entries, l => l.Add((logLevel, formatter(state, exception))));
}
