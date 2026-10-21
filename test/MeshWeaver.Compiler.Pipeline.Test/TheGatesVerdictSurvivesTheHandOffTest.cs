using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// MeshWeaver#6930 / review of #7015 — the boot sweep's batch hands a type the untracked-module gate
/// refuses to the compile watcher. Two ways that hand-off could still lose the gate's verdict, each
/// pinned with a REAL image bundle (<see cref="BundleWriter"/>) consumed through the real
/// <see cref="IPrebuiltAssemblyConsumer"/>:
/// <list type="bullet">
///   <item><description><see cref="OfferedOnly"/>: the type is module content ONLY because a bundle
///   names it (the production <c>SocialMedia/SocialTests</c> shape — the bundle is declined on its
///   dependency record), and its record carries a pending <c>RequestedReleaseForce</c>. A force makes
///   the watcher skip its adoption pass, which is its only source of the offer witness; without
///   adoption provenance the gate would then read "authored content" and Roslyn would run.</description></item>
///   <item><description><see cref="AdoptedHere"/>: the gate's offer pass ADOPTS the bundle on a
///   readiness-gated (provisional) process, which holds the shared stamp. The record the watcher
///   reads still says "nothing to serve", so a hand-off would PARK a type this process can serve — a
///   false bake failure. It must be reported baked from this process's own adoption.</description></item>
/// </list>
/// </summary>
public abstract class TheGatesVerdictSurvivesTheHandOffTestBase(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    protected const string SeededMvid = "0000cccc0000cccc";

    private readonly string imageDirectory = Path.Combine(
        Path.GetTempPath(), "mw-6930-image-" + Guid.NewGuid().ToString("N")[..12]);

    /// <summary>The bundle entries the image ships for this case.</summary>
    protected abstract IReadOnlyList<BundleWriter.AssemblyEntry> Entries { get; }

    /// <summary>Extra files laid into the image bundle directory beside the bundle (file name → bytes).</summary>
    protected virtual IReadOnlyDictionary<string, byte[]> ExtraImageFiles { get; } =
        new Dictionary<string, byte[]>();

    /// <summary>Extra services for this case (an admission authority).</summary>
    protected virtual void AddServices(IServiceCollection services) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddPrebuiltAssemblyConsumption();
                AddServices(services);
                WriteImageBundle();
                // Layer the image bundle directory onto the host's configuration, never replace it.
                var configured = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration));
                if (configured is not null)
                    services.Remove(configured);
                return services.AddSingleton<IConfiguration>(sp =>
                {
                    var configuration = new ConfigurationBuilder();
                    if (Materialise(configured, sp) is { } host)
                        configuration.AddConfiguration(host);
                    return configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [ShippedPrebuiltBundles.DirectoryConfigKey] = imageDirectory,
                    }).Build();
                });
            });

    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance is IConfiguration instance ? instance
            : throw new InvalidOperationException(
                "The IConfiguration registration is neither a factory nor an instance — update this "
                + "hook rather than replacing the host's configuration.");

    private void WriteImageBundle()
    {
        Directory.CreateDirectory(imageDirectory);
        foreach (var (name, bytes) in ExtraImageFiles)
            File.WriteAllBytes(Path.Combine(imageDirectory, name), bytes);
        using var file = File.Create(Path.Combine(imageDirectory, "image.zip"));
        BundleWriter.Write(
            file, plugin: "Leftover", version: "1.0.0",
            frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid,
            assemblies: Entries);
    }

    /// <summary>Real PE bytes with a real MVID — the test assembly, as the adoption tests use.</summary>
    protected static byte[] AssemblyBytes() =>
        File.ReadAllBytes(typeof(TheGatesVerdictSurvivesTheHandOffTestBase).Assembly.Location);

    protected IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    protected NodeTypeCompileParkRegistry Parks =>
        Mesh.ServiceProvider.GetRequiredService<NodeTypeCompileParkRegistry>();

    /// <summary>Authored-looking record: no adoption provenance, nothing to serve.</summary>
    protected async Task CreateType(
        string path, bool force, IReadOnlyList<string>? sources = null, bool moduleContent = false,
        CompilationStatus status = CompilationStatus.Ok)
    {
        var node = MeshNode.FromPath(path) with
        {
            Name = path[(path.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = status,
                LatestAssemblyMvid = SeededMvid,
                Sources = sources,
                AdoptedSourceFingerprint = moduleContent ? "bundlefingerprint0000000" : null,
                BuildProvenance = moduleContent ? BuildProvenance.AdoptedVerified : BuildProvenance.Compiled,
                RequestedReleaseForce = force,
            },
        };
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().ImpersonateAsSystem())
            await MeshService.CreateNode(node).Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>A real Code node — the source a type owns, or shares with another.</summary>
    protected async Task CreateCode(string ns, string id, string code)
    {
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().ImpersonateAsSystem())
            await MeshService.CreateNode(new MeshNode(id, ns)
                {
                    NodeType = "Code",
                    State = MeshNodeState.Active,
                    Content = new CodeConfiguration { Language = "csharp", Code = code },
                })
                .Should().Within(TestTimeouts.Convergence)
                .Emit(cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>A real <c>{partition}/_GitSync</c> naming a repository — "tracked".</summary>
    protected async Task TrackPartition(string partition)
    {
        await MeshService.CreateNode(new MeshNode(GitHubSyncService.ConfigId, partition)
            {
                Name = "GitHub Sync",
                NodeType = GitHubSyncService.ConfigNodeType,
                State = MeshNodeState.Active,
                Content = new GitHubSyncConfig
                {
                    RepositoryUrl = "https://github.com/Systemorph/Example",
                    Branch = "main",
                },
            })
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);
        (await NodeTypeCompilationHelpers.PartitionTracksSources(Mesh, $"{partition}/Widget")
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeTrue("a _GitSync naming a repository is what 'tracked' means");
    }

    /// <summary>The record once something settled it: an Error, or an Ok whose MVID moved (a Roslyn pass).</summary>
    protected async Task<NodeTypeDefinition> Settled(string path, string because)
        => (await Mesh.GetMeshNodeStream(path)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(d => d is not null
                        && (d.CompilationStatus == CompilationStatus.Error
                            || (d.CompilationStatus == CompilationStatus.Ok
                                && !string.Equals(d.LatestAssemblyMvid, SeededMvid, StringComparison.Ordinal))),
                because, TestContext.Current.CancellationToken))!;

    /// <summary>
    /// The owner's decision for an INCOMPLETE offer discovery (#7015 review), asserted through the real
    /// sweep: the never-adopted type in a partition with no sync source is PARKED with the unknown-
    /// membership reason and never reaches Roslyn; the same kind of record in a TRACKED partition, in
    /// the same pass, compiles.
    /// </summary>
    protected async Task AssertUnknownMembershipParks_AndTrackedCompiles(string untrackedType, string trackedType)
    {
        await TrackPartition(PartitionOf(trackedType));
        await CreateType(untrackedType, force: false);
        await CreateType(trackedType, force: false);

        var outcomes = await Sweep();
        outcomes.Where(o => string.Equals(o.TypePath, untrackedType, StringComparison.OrdinalIgnoreCase))
            .Should().NotContain(o => o.Status == PreWarmStatus.Compiled, "the batch never compiles it");

        var parked = await Settled(untrackedType, "the unknown-membership type settles");
        parked.CompilationStatus.Should().Be(CompilationStatus.Error,
            $"its membership is unknown and its partition tracks no source — park it; error: {parked.CompilationError}");
        parked.CompilationError.Should().Contain("Unknown module membership").And.Contain("did not complete")
            .And.Contain("MeshWeaver#6930");
        parked.LatestAssemblyMvid.Should().Be(SeededMvid, "no Roslyn pass ran");
        Parks.GetCompileAttemptCount(untrackedType).Should().Be(0, "no compile was dispatched");

        var compiled = await Settled(trackedType, "the tracked type settles");
        compiled.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"a partition with a sync source compiles whatever the discovery said; error: {compiled.CompilationError}");
        compiled.LatestAssemblyMvid.Should().NotBe(SeededMvid, "a real Roslyn pass wrote real coordinates");
    }

    private static string PartitionOf(string path) => path[..path.IndexOf('/')];

    protected async Task<IList<PreWarmOutcome>> Sweep()
        => await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, perTypeBudget: TestTimeouts.Convergence, batchBake: true, betweenTypes: TimeSpan.Zero)
            .ToList()
            .Should().Within(TestTimeouts.CrossSilo * 3)
            .Emit("the sweep completes", cancellationToken: TestContext.Current.CancellationToken);

    protected Task<NodeTypeDefinition?> Current(string path)
        => Mesh.GetMeshNodeStream(path)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Where(d => d is not null)
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken);
}

/// <summary>The offered-only, FORCED record — see <see cref="TheGatesVerdictSurvivesTheHandOffTestBase"/>.</summary>
public class OfferedOnly(ITestOutputHelper output) : TheGatesVerdictSurvivesTheHandOffTestBase(output)
{
    private const string TypePath = "untracked/Offered";
    private const string PersistedPending = "untracked/PersistedPending";
    private const string LateInTheWalk = "untracked/LateInTheWalk";

    /// <summary>Declined on its dependency record exactly as in production: a module minimum this
    /// environment cannot compare ("NotChecked") — so the bundle NAMES the type and adopts nothing.</summary>
    protected override IReadOnlyList<BundleWriter.AssemblyEntry> Entries =>
    [
        .. new[] { TypePath, PersistedPending, LateInTheWalk }.Select(path =>
            new BundleWriter.AssemblyEntry(path, () => new MemoryStream(AssemblyBytes()),
                Dependencies: new Dictionary<string, string> { ["MeshWeaver.Leftover"] = "min:1.4.1" })),
    ];

    private async Task AssertParkedWithoutRoslyn(string path)
    {
        var settled = await Mesh.GetMeshNodeStream(path)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(d => d is not null
                        && (d.CompilationStatus == CompilationStatus.Error
                            || (d.CompilationStatus == CompilationStatus.Ok
                                && !string.Equals(d.LatestAssemblyMvid, SeededMvid, StringComparison.Ordinal))),
                "the type settles", TestContext.Current.CancellationToken);
        settled!.CompilationStatus.Should().Be(CompilationStatus.Error,
            $"a bundle names the type, so the gate refuses a local compile; error: {settled.CompilationError}");
        settled.CompilationError.Should().Contain("tracks no source").And.Contain("DECLINED");
        settled.LatestAssemblyMvid.Should().Be(SeededMvid, "no Roslyn pass ran");
        Parks.GetCompileAttemptCount(path).Should().Be(0);
    }

    /// <summary>
    /// #7015 review — a record a previous process left PERSISTED at <c>Pending</c> with a force: the
    /// watcher dispatches it the moment the type's hub activates, before any hand-off write can land,
    /// so the offer witness must come from the watcher's own pass even on a forced flip.
    /// </summary>
    [Fact]
    public async Task APersistedPendingForcedOfferedOnlyType_IsParked_NeverCompiled()
    {
        await CreateType(PersistedPending, force: true, status: CompilationStatus.Pending);
        await AssertParkedWithoutRoslyn(PersistedPending);

        var outcomes = await Sweep();
        outcomes.Where(o => string.Equals(o.TypePath, PersistedPending, StringComparison.OrdinalIgnoreCase))
            .Should().NotContain(o => o.Status == PreWarmStatus.Compiled);
        await AssertParkedWithoutRoslyn(PersistedPending);
    }

    /// <summary>
    /// #7015 review — the offer pass's budget runs out before it reaches the bundle naming a type:
    /// that type's membership is UNKNOWN, so it must still be refused a batch compile (fail closed).
    /// </summary>
    [Fact]
    public async Task AnOfferPassCutShortByItsBudget_FailsClosed()
    {
        var refused = await NodeTypeCompilationHelpers.RefusedByUntrackedModuleGate(
                Mesh,
                new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
                {
                    [LateInTheWalk] = new NodeTypeDefinition { Configuration = "config => config" },
                },
                null,
                offerBudget: TimeSpan.Zero)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the question answers once", cancellationToken: TestContext.Current.CancellationToken);
        refused.Should().Contain(LateInTheWalk,
            "a pass that did not reach the bundle has not established that no bundle names the type — "
            + "reading 'not witnessed' as 'authored' sends a module's content to Roslyn");
    }

    [Fact]
    public async Task AnOfferedOnlyForcedType_IsParkedByTheGate_NeverCompiled()
    {
        await CreateType(TypePath, force: true);

        var outcomes = await Sweep();

        var outcome = outcomes.Where(o => string.Equals(o.TypePath, TypePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        outcome.Should().ContainSingle("the sweep REACHED the type");
        outcome[0].Status.Should().NotBe(PreWarmStatus.Compiled,
            $"a bundle named the type, so it is a module's content and the gate refuses a local compile; "
            + $"detail: {outcome[0].Detail}");

        var settled = await Mesh.GetMeshNodeStream(TypePath)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(d => d is not null
                        && (d.CompilationStatus == CompilationStatus.Error
                            || (d.CompilationStatus == CompilationStatus.Ok
                                && !string.Equals(d.LatestAssemblyMvid, SeededMvid, StringComparison.Ordinal))),
                "the hand-off settles the type", TestContext.Current.CancellationToken);
        settled!.CompilationStatus.Should().Be(CompilationStatus.Error,
            $"the gate's verdict survives the hand-off; error: {settled.CompilationError}");
        settled.CompilationError.Should().Contain("tracks no source").And.Contain("DECLINED",
            "the watcher saw the same offer the batch classified on");
        settled.LatestAssemblyMvid.Should().Be(SeededMvid, "no Roslyn pass ran");
        Parks.GetCompileAttemptCount(TypePath).Should().Be(0);
    }
}

/// <summary>The gate's own pass ADOPTS, on a provisional process — see
/// <see cref="TheGatesVerdictSurvivesTheHandOffTestBase"/>.</summary>
public class AdoptedHere(ITestOutputHelper output) : TheGatesVerdictSurvivesTheHandOffTestBase(output)
{
    private const string TypePath = "untracked/Adopted";

    protected override IReadOnlyList<BundleWriter.AssemblyEntry> Entries =>
    [
        new BundleWriter.AssemblyEntry(TypePath, () => new MemoryStream(AssemblyBytes())),
    ];

    /// <summary>A readiness-gated process whose bake is still measuring: adoption stamps are HELD.</summary>
    protected override void AddServices(IServiceCollection services)
        => services.AddSingleton<IMeshAdmissionAuthority>(new StillMeasuring());

    [Fact]
    public async Task ATypeTheGatesPassAdoptedOnThisProcess_IsBaked_NotParked()
    {
        await CreateType(TypePath, force: false);

        var outcomes = await Sweep();

        var outcome = outcomes.Where(o => string.Equals(o.TypePath, TypePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        outcome.Should().ContainSingle("the sweep REACHED the type");
        outcome[0].ReachedUsableBuild.Should().BeTrue(
            $"this process adopted the bundle — its build is usable HERE though the shared stamp is held; "
            + $"status {outcome[0].Status}, detail: {outcome[0].Detail}");
        Mesh.ServiceProvider.GetRequiredService<NodeTypeAdoptionRegistry>().AdoptedStamps.Keys
            .Should().Contain(TypePath, "precondition: the gate's offer pass adopted it on this process");
        Parks.IsParked(TypePath).Should().BeFalse("a type this process can serve is never parked");
        (await Current(TypePath))!.CompilationStatus.Should().NotBe(CompilationStatus.Error,
            "the shared record must not be parked over a build this process adopted");
    }

    private sealed class StillMeasuring : IMeshAdmissionAuthority
    {
        public MeshAdmission Admission => MeshAdmission.Provisional;

        public string AdmissionReason => "test: the bake is still measuring";
    }
}

/// <summary>
/// #7015 review — a type the gate's own pass ADOPTED on this process is baked, and a failed upstream
/// whose sources it shares cannot block it: <c>untracked/UpstreamA</c> is module content with
/// nothing to serve and no bundle (parked by the gate), <c>untracked/AdoptedB</c> compiles A's
/// shared source into itself and adopts a bundle in the gate's pass, and <c>untracked/DependentC</c>
/// shares B's source. B must be reported baked — never <c>UpstreamFailed</c> by A — and C must not
/// inherit a failure through B.
/// </summary>
public class AdoptedBehindAParkedUpstream(ITestOutputHelper output) : TheGatesVerdictSurvivesTheHandOffTestBase(output)
{
    private const string UpstreamA = "untracked/UpstreamA";
    private const string AdoptedB = "untracked/AdoptedB";
    private const string DependentC = "untracked/DependentC";

    protected override IReadOnlyList<BundleWriter.AssemblyEntry> Entries =>
    [
        new BundleWriter.AssemblyEntry(AdoptedB, () => new MemoryStream(AssemblyBytes())),
    ];

    /// <summary>A readiness-gated process whose bake is still measuring: adoption stamps are HELD.</summary>
    protected override void AddServices(IServiceCollection services)
        => services.AddSingleton<IMeshAdmissionAuthority>(new StillMeasuring());

    [Fact]
    public async Task AnAdoptedTypeBehindAParkedUpstream_IsBaked_AndItsDependentsAreNotBlocked()
    {
        await CreateCode($"{UpstreamA}/Source", "Lib", "public static class LibA { public static int A() => 1; }");
        await CreateCode($"{AdoptedB}/Source", "Lib", "public static class LibB { public static int B() => 2; }");
        await CreateType(UpstreamA, force: false, moduleContent: true);
        await CreateType(AdoptedB, force: false,
            sources: ["namespace:Source scope:subtree", $"shared=@{UpstreamA}/Source/Lib"]);
        await CreateType(DependentC, force: false,
            sources: ["namespace:Source scope:subtree", $"shared=@{AdoptedB}/Source/Lib"]);

        var outcomes = await Sweep();
        PreWarmOutcome Of(string path) => outcomes.Single(o =>
            string.Equals(o.TypePath, path, StringComparison.OrdinalIgnoreCase));

        Of(UpstreamA).ReachedUsableBuild.Should().BeFalse(
            $"precondition: the upstream is parked by the gate; status {Of(UpstreamA).Status}");
        Of(AdoptedB).Status.Should().Be(PreWarmStatus.AlreadyBaked,
            $"B adopted a usable bundle on this process and compiles nothing — a failed upstream "
            + $"cannot block it; status {Of(AdoptedB).Status}, detail: {Of(AdoptedB).Detail}");
        Of(DependentC).Status.Should().NotBe(PreWarmStatus.UpstreamFailed,
            $"C's upstream B is baked, so no failure reaches C through it; detail: {Of(DependentC).Detail}");
        Of(DependentC).BlockedBy.Should().BeNull("nothing upstream of C failed");
    }

    private sealed class StillMeasuring : IMeshAdmissionAuthority
    {
        public MeshAdmission Admission => MeshAdmission.Provisional;

        public string AdmissionReason => "test: the bake is still measuring";
    }
}

/// <summary>
/// #7015 review — the offer pass RETURNS NORMALLY after a discovery fault: the bundle walk folds a
/// bundle it cannot read into its tally (<c>Faulted</c>) and carries on, and the consumer reports
/// only the adopted count. The unreadable archive is the one that would have named
/// <c>untracked/InTheUnreadableBundle</c> — nothing else does — so the pass never learned whether a
/// bundle names it. Its membership is unknown, and it must not be classified as authored content
/// and handed to the batch compile. The readable bundle beside it names only another type, so the
/// pass itself completes and returns normally.
/// </summary>
public class DiscoveryFault(ITestOutputHelper output) : TheGatesVerdictSurvivesTheHandOffTestBase(output)
{
    private const string TypePath = "untracked/InTheUnreadableBundle";

    protected override IReadOnlyDictionary<string, byte[]> ExtraImageFiles { get; } =
        new Dictionary<string, byte[]> { ["a-unreadable.zip"] = System.Text.Encoding.UTF8.GetBytes("not a zip archive") };

    protected override IReadOnlyList<BundleWriter.AssemblyEntry> Entries =>
    [
        new BundleWriter.AssemblyEntry("untracked/SomethingElse", () => new MemoryStream(AssemblyBytes())),
    ];

    [Fact]
    public async Task ASwallowedDiscoveryFault_FailsClosed()
    {
        var refused = await NodeTypeCompilationHelpers.RefusedByUntrackedModuleGate(
                Mesh,
                new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
                {
                    [TypePath] = new NodeTypeDefinition { Configuration = "config => config" },
                },
                null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the question answers once", cancellationToken: TestContext.Current.CancellationToken);
        refused.Should().Contain(TypePath,
            "the pass returned normally but could not read every bundle — 'not witnessed' is not "
            + "'authored', so the type must stay out of the batch's direct compile");
    }

    [Fact]
    public Task ThroughTheSweep_TheUnknownTypeParks_AndATrackedTypeCompiles()
        => AssertUnknownMembershipParks_AndTrackedCompiles(TypePath, "tracked/Authored");
}

/// <summary>
/// #7015 review — a VALID ZIP with no <c>meshweaver/manifest.json</c> states no framework and names
/// nothing; it is HOLLOW (discovery incomplete), never a framework decline that would let the pass
/// read as complete.
/// </summary>
public class ManifestLessBundle(ITestOutputHelper output) : TheGatesVerdictSurvivesTheHandOffTestBase(output)
{
    private const string TypePath = "untracked/InTheManifestLessBundle";

    private static byte[] ZipWithoutManifest()
    {
        using var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("lib/Something.dll");
            using var stream = entry.Open();
            stream.Write(AssemblyBytes());
        }
        return buffer.ToArray();
    }

    protected override IReadOnlyDictionary<string, byte[]> ExtraImageFiles { get; } =
        new Dictionary<string, byte[]> { ["a-no-manifest.zip"] = ZipWithoutManifest() };

    protected override IReadOnlyList<BundleWriter.AssemblyEntry> Entries =>
    [
        new BundleWriter.AssemblyEntry("untracked/SomethingElse", () => new MemoryStream(AssemblyBytes())),
    ];

    [Fact]
    public async Task AManifestLessBundle_IsIncompleteDiscovery_NotADecline()
    {
        var refused = await NodeTypeCompilationHelpers.RefusedByUntrackedModuleGate(
                Mesh,
                new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
                {
                    [TypePath] = new NodeTypeDefinition { Configuration = "config => config" },
                },
                null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the question answers once", cancellationToken: TestContext.Current.CancellationToken);
        refused.Should().Contain(TypePath,
            "a bundle with no manifest names an unknown set of types — its membership is not established");
    }

    [Fact]
    public Task ThroughTheSweep_TheUnknownTypeParks_AndATrackedTypeCompiles()
        => AssertUnknownMembershipParks_AndTrackedCompiles(TypePath, "tracked/Authored");
}

/// <summary>
/// #7015 review — a manifest that states NO framework identity is HOLLOW (discovery incomplete), never
/// a framework decline: it is not KNOWN to be another framework's, and declining it before its entries
/// are witnessed would let the pass read as complete although the bundle names
/// <c>untracked/InTheIdentityLessBundle</c>.
/// </summary>
public class IdentityLessBundle(ITestOutputHelper output) : TheGatesVerdictSurvivesTheHandOffTestBase(output)
{
    private const string TypePath = "untracked/InTheIdentityLessBundle";

    private static byte[] ZipWithIdentityLessManifest()
    {
        using var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry(NuGetPackageWriter.ManifestEntry);
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write(
                    "{\"plugin\":\"Anonymous\",\"version\":\"1.0.0\",\"assemblies\":["
                    + $"{{\"nodePath\":\"{TypePath}\",\"assembly\":\"Anonymous.dll\"}}]}}");
            var dll = archive.CreateEntry($"{NuGetPackageWriter.AssemblyFolder}/Anonymous.dll");
            using var stream = dll.Open();
            stream.Write(AssemblyBytes());
        }
        return buffer.ToArray();
    }

    protected override IReadOnlyDictionary<string, byte[]> ExtraImageFiles { get; } =
        new Dictionary<string, byte[]> { ["a-no-identity.zip"] = ZipWithIdentityLessManifest() };

    protected override IReadOnlyList<BundleWriter.AssemblyEntry> Entries =>
    [
        new BundleWriter.AssemblyEntry("untracked/SomethingElse", () => new MemoryStream(AssemblyBytes())),
    ];

    [Fact]
    public async Task AnIdentityLessBundle_IsIncompleteDiscovery_NotADecline()
    {
        var refused = await NodeTypeCompilationHelpers.RefusedByUntrackedModuleGate(
                Mesh,
                new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
                {
                    [TypePath] = new NodeTypeDefinition { Configuration = "config => config" },
                },
                null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the question answers once", cancellationToken: TestContext.Current.CancellationToken);
        refused.Should().Contain(TypePath,
            "a bundle that states no framework identity names this type, and whether its bytes are "
            + "for this mesh is unknown — its membership is not established, so it is not authored");
    }
}

/// <summary>
/// #7015 review — ONE bundle that carries an assembly for <c>untracked/Sibling</c> and records a
/// producer MISS for <c>untracked/Widget</c>. The miss names Widget as this bundle's content as
/// surely as an entry would, so the never-adopted Widget is a module's content in an untracked
/// partition: parked, never compiled. Sibling's adoption from the same bundle is kept.
/// </summary>
public class MixedSuccessAndMissBundle(ITestOutputHelper output) : TheGatesVerdictSurvivesTheHandOffTestBase(output)
{
    private const string Sibling = "untracked/Sibling";
    private const string Widget = "untracked/Widget";

    private static byte[] MixedZip()
    {
        using var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry(NuGetPackageWriter.ManifestEntry).Open()))
                writer.Write(
                    "{\"plugin\":\"Mixed\",\"version\":\"1.0.0\","
                    + $"\"frameworkMvid\":\"{PrebuiltAssemblySeeder.LiveFrameworkMvid}\","
                    + $"\"assemblies\":[{{\"nodePath\":\"{Sibling}\",\"assembly\":\"Sibling.dll\"}}],"
                    + $"\"misses\":[\"{Widget}: no artifact for this lane\"]}}");
            using var dll = archive.CreateEntry($"{NuGetPackageWriter.AssemblyFolder}/Sibling.dll").Open();
            dll.Write(AssemblyBytes());
        }
        return buffer.ToArray();
    }

    protected override IReadOnlyDictionary<string, byte[]> ExtraImageFiles { get; } =
        new Dictionary<string, byte[]> { ["a-mixed.zip"] = MixedZip() };

    protected override IReadOnlyList<BundleWriter.AssemblyEntry> Entries =>
    [
        new BundleWriter.AssemblyEntry("untracked/SomethingElse", () => new MemoryStream(AssemblyBytes())),
    ];

    [Fact]
    public async Task AMissInABundleThatAlsoAdopts_ParksTheMissedType_AndKeepsTheSibling()
    {
        await CreateType(Sibling, force: false);
        await CreateType(Widget, force: false);

        var outcomes = await Sweep();
        outcomes.Where(o => string.Equals(o.TypePath, Widget, StringComparison.OrdinalIgnoreCase))
            .Should().NotContain(o => o.Status == PreWarmStatus.Compiled, "the batch never compiles it");

        var widget = await Settled(Widget, "the missed type settles");
        widget.CompilationStatus.Should().Be(CompilationStatus.Error,
            $"the bundle's miss names Widget — a module's content in a partition that tracks no source; "
            + $"error: {widget.CompilationError}");
        widget.LatestAssemblyMvid.Should().Be(SeededMvid, "no Roslyn pass ran");
        Parks.GetCompileAttemptCount(Widget).Should().Be(0, "no compile was dispatched");

        var sibling = await Settled(Sibling, "the sibling settles");
        sibling.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"the same bundle's successful entry still adopts; error: {sibling.CompilationError}");
        sibling.LatestAssemblyMvid.Should().NotBe(SeededMvid, "the bundle's build was stamped");
    }
}

/// <summary>
/// #7015 review — a module-only bundle (a manifest that DECLARES a module, lists no NodeType
/// assemblies and records no producer misses) is a valid, COMPLETE delivery: it names no NodeType,
/// so it leaves no membership unknown. Beside it, an authored type in an untracked partition is
/// neither refused by the gate nor parked — it compiles, as it did before the gate existed.
/// </summary>
public class ModuleOnlyBundle(ITestOutputHelper output) : TheGatesVerdictSurvivesTheHandOffTestBase(output)
{
    private const string TypePath = "untracked/AuthoredBesideAModule";

    private static byte[] ModuleOnlyZip()
    {
        using var buffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
                   buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry(NuGetPackageWriter.ManifestEntry);
            using var writer = new StreamWriter(manifest.Open());
            writer.Write(
                "{\"plugin\":\"ModuleOnly\",\"version\":\"1.0.0\","
                + $"\"frameworkMvid\":\"{PrebuiltAssemblySeeder.LiveFrameworkMvid}\","
                + "\"assemblies\":[],\"module\":{\"assemblyName\":\"ModuleOnly.Module\",\"assemblies\":[]}}");
        }
        return buffer.ToArray();
    }

    protected override IReadOnlyDictionary<string, byte[]> ExtraImageFiles { get; } =
        new Dictionary<string, byte[]> { ["a-module-only.zip"] = ModuleOnlyZip() };

    protected override IReadOnlyList<BundleWriter.AssemblyEntry> Entries =>
    [
        new BundleWriter.AssemblyEntry("untracked/SomethingElse", () => new MemoryStream(AssemblyBytes())),
    ];

    [Fact]
    public async Task AModuleOnlyBundle_IsCompleteDiscovery_AndTheAuthoredTypeIsNotRefused()
    {
        var refused = await NodeTypeCompilationHelpers.RefusedByUntrackedModuleGate(
                Mesh,
                new Dictionary<string, NodeTypeDefinition?>(StringComparer.OrdinalIgnoreCase)
                {
                    [TypePath] = new NodeTypeDefinition { Configuration = "config => config" },
                },
                null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the question answers once", cancellationToken: TestContext.Current.CancellationToken);
        refused.Should().NotContain(TypePath,
            "a bundle that declares it ships only a module names no NodeType — the pass is complete, "
            + "and an authored type no bundle names is authored content");
    }

    [Fact]
    public async Task ThroughTheSweep_TheAuthoredTypeCompiles()
    {
        await CreateType(TypePath, force: false);

        await Sweep();

        var settled = await Settled(TypePath, "the authored type settles");
        settled.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"authored content beside a complete module-only bundle compiles; error: {settled.CompilationError}");
        settled.LatestAssemblyMvid.Should().NotBe(SeededMvid, "a real Roslyn pass wrote real coordinates");
    }
}
