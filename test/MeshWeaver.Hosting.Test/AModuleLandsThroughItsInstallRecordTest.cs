using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A module lands — and is removed — through its install record's own hub</b>
/// (MeshWeaver.Plugins#5055, <see cref="ModuleLandingOwner"/>, <c>Doc/Architecture/PackageUninstall</c>).
/// A download that outlived an uninstall must not land after the uninstall's tombstone; the owner
/// executes one module operation at a time, resumes one a dead activation left executing, and a fresh
/// package with no record still lands. Real mesh, real record hubs, the real
/// <see cref="ModuleLandingService"/> landing real assembly bytes into a throwaway module root.
/// </summary>
public class AModuleLandsThroughItsInstallRecordTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly string moduleRoot =
        Path.Combine(Path.GetTempPath(), "mw-5055-b2-" + Guid.NewGuid().ToString("N")[..8]);

    // The park: the FIRST landing that reaches its recording window waits there until the test
    // releases it — a release INTO a worker the test deliberately parks, polled under a bounded spin.
    private volatile int parkFirst;
    private int entered;
    private volatile int parked;
    private volatile int release;
    // A second park, inside a REMOVAL (after its tombstone, before its projection) — a removal in flight.
    private volatile int parkRemoval;
    private volatile int removalParked;
    private volatile int releaseRemoval;
    // Fault seams (#7974 review, round 5), each armed for ONE hit and read through Interlocked: the
    // owner's ANSWER write fails once; a landing's recording throws an I/O fault once.
    private int failAnswerOnce;
    private int answerFailures;
    private int failRecordingOnce;
    private ModuleLandingService? landing;
    private readonly FakeRegistry registry = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false })
                .AddSingleton<IHttpClientFactory>(new FakeFactory(registry))
                // Registered AFTER the catalog's own, so this is the instance every hub resolves.
                .AddSingleton(_ => landing ??= new ModuleLandingService(null, moduleRoot, beforeRecording: _ =>
                {
                    if (Interlocked.Exchange(ref failRecordingOnce, 0) == 1)
                        throw new IOException("the shared module volume hiccupped (test fault)");
                    if (parkFirst == 0 || Interlocked.Increment(ref entered) != 1)
                        return;
                    parked = 1;
                    SpinWait.SpinUntil(() => release == 1, TestTimeouts.Convergence);
                }, beforeProjecting: _ =>
                {
                    if (parkRemoval == 0)
                        return;
                    removalParked = 1;
                    SpinWait.SpinUntil(() => releaseRemoval == 1, TestTimeouts.Convergence);
                })
                {
                    OwnerAnswerSeam = node =>
                    {
                        if (Interlocked.Exchange(ref failAnswerOnce, 0) != 1)
                            return node;
                        Interlocked.Increment(ref answerFailures);
                        throw new InvalidOperationException("the owner's answer write failed (test fault)");
                    },
                }));

    private ModuleLandingService Landing => Mesh.ServiceProvider.GetRequiredService<ModuleLandingService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private static string RecordPath(string id) => $"{PackageInstaller.InstalledPartition}/{id}";

    private static byte[] RealAssemblyBytes => File.ReadAllBytes(typeof(BundleReader).Assembly.Location);

    /// <summary>One landing of <paramref name="module"/> at <paramref name="version"/>; the version
    /// rides a static asset so two versions are two contents.</summary>
    private static ModuleLandingArguments Bytes(string module, string version, string? packagePath) =>
        new(module, [(module + ".dll", RealAssemblyBytes)])
        {
            PackagePath = packagePath,
            Version = version,
            StaticAssets = [("wwwroot/version.txt", Encoding.UTF8.GetBytes(version))],
        };

    private static MeshNode Record(string id, string module) =>
        MeshNode.FromPath(RecordPath(id)) with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = id,
            State = MeshNodeState.Active,
            Content = new PackageManifest { Id = id, Name = id, Source = "Plugins", Version = "1.0.0", TargetPartition = id, Module = module },
        };

    private Task WriteRecord(string id, string module, CancellationToken ct) =>
        InstallRecordGate.Write(Mesh, Record(id, module), PackageRecordWriteIntent.CreateAllowed, NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);

    private IObservable<ModuleLandingVerdict> Land(string module, string version, string? packagePath, string? intent = null,
        bool recordAtPlan = false) =>
        ModuleLandingOwner.Land(Mesh, Landing, Bytes(module, version, packagePath), intent, recordAtPlan, NullLogger.Instance);

    /// <summary>Creates a node as System, content and all — how a test puts a record into a state the
    /// owner wrote before a replica died, or a Package node where no install record belongs.</summary>
    private async Task Create(MeshNode node, CancellationToken ct)
    {
        var created = await Access.RunAsSystem(() => Mesh.NodeOperationIssuingHub()
                .Observe<CreateNodeResponse>(new CreateNodeRequest(node)))
            .FirstAsync().Select(d => d.Message).Timeout(TestTimeouts.Convergence).Await(ct);
        created.Success.Should().BeTrue(created.Error ?? "");
    }

    private static PackageModuleOperation ExecutingRemove(string module, string request) => new()
    {
        Kind = PackageModuleOperationKind.Remove,
        Module = module,
        Request = request,
        State = PackageModuleOperationState.Executing,
        At = DateTimeOffset.UtcNow.AddMinutes(-1),
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
    };

    private Task<ModuleActivationEntry?> Entry(string module, CancellationToken ct) =>
        Landing.GetActivation()
            .Select(list => list.Entries.FirstOrDefault(e => string.Equals(e.Name, module, StringComparison.OrdinalIgnoreCase)))
            .Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<PackageManifest> Live(string id, Func<PackageManifest, bool> until, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetMeshNodeStream(RecordPath(id)))
            .Select(n => n.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions))
            .OfType<PackageManifest>()
            .Where(until)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>
    /// (1) THE B2 race: a download that started before the uninstall is released after phase 1's
    /// tombstone. Through the owner it is REFUSED, by name — while the retirement is held, and again
    /// once the record is gone (no owner; the module's own tombstone refuses it). Negative control, in
    /// the test: the same bytes through a DIRECT landing land, so the refusal is the owner's and not a
    /// landing that could not have succeeded. Negative control (measured): with the owner's retirement
    /// check removed from <see cref="ModuleLandingOwner"/>'s promotion, the released landing is applied
    /// and the module is re-enabled.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ADownloadReleasedAfterTheTombstone_IsRefused_ADirectLandingLands()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Held.Module";
        const string request = "Admin/_PackageUninstall/req-held";
        await WriteRecord("LandHeld", module, ct);
        (await Land(module, "1.0.0", RecordPath("LandHeld")).Timeout(TestTimeouts.Convergence).Await(ct))
            .Applied.Should().BeTrue("precondition: the module lands through its record");

        // Phase 1: the retirement, then the removal through the same owner.
        (await InstallRecordGate.BeginRetirement(Mesh, "LandHeld", request, "test", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull("the retirement is granted");
        (await ModuleLandingOwner.Remove(Mesh, "LandHeld", module, request, NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be("landed generation disabled");
        (await Entry(module, ct))!.Enabled.Should().BeFalse("the tombstone is written");

        // The held download is released NOW.
        var refused = await Land(module, "1.1.0", RecordPath("LandHeld")).Timeout(TestTimeouts.Convergence).Await(ct);
        refused.Applied.Should().BeFalse("a landing after the tombstone, while the package is retired, is refused at the owner");
        refused.Detail.Should().Contain("being retired by " + request);
        (await Entry(module, ct))!.Enabled.Should().BeFalse("the refused landing re-enabled nothing");

        // The uninstall completes — the record is gone — and another held download is released.
        (await PackageInstaller.RemoveInstalledRecord(Mesh, "LandHeld", NullLogger.Instance).Take(1)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeTrue();
        var afterRecord = await Land(module, "1.2.0", RecordPath("LandHeld")).Timeout(TestTimeouts.Convergence).Await(ct);
        afterRecord.Applied.Should().BeFalse("with no owner left, the module's own tombstone refuses it");
        afterRecord.Detail.Should().Contain("uninstalled after this landing was planned");
        (await Entry(module, ct))!.Enabled.Should().BeFalse();

        // Negative control: the same kind of bytes, landed DIRECTLY, land.
        await Landing.LandModule(module, [(module + ".dll", RealAssemblyBytes)], version: "1.3.0",
                staticAssets: [("wwwroot/version.txt", Encoding.UTF8.GetBytes("1.3.0"))])
            .Timeout(TestTimeouts.Convergence).Await(ct);
        (await Entry(module, ct))!.Enabled.Should().BeTrue("a direct landing re-enables the module — what the owner prevents");
    }

    /// <summary>
    /// (2) An owner operation interrupted by a process restart RESUMES: the record holds a landing the
    /// owner had promoted (persisted <see cref="PackageModuleOperationState.Executing"/>, bytes staged)
    /// and never answered — the replica that started it died. A fresh activation of the record's hub
    /// executes it, answers it, and drops its staged bytes. Negative control (measured): with the
    /// executor running only operations its own activation promoted, the landing is never answered
    /// and this times out red.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnInterruptedOwnerOperation_IsResumedByTheNextActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Resumed.Module";
        const string key = "module-land-interrupted";
        await Landing.StageLanding(key, Bytes(module, "2.0.0", RecordPath("LandResume")))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var record = Record("LandResume", module);
        var interrupted = record with
        {
            Content = record.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)! with
            {
                ModuleOperations = ImmutableDictionary<string, PackageModuleOperation>.Empty.Add(key, new PackageModuleOperation
                {
                    Kind = PackageModuleOperationKind.Land,
                    Module = module,
                    PackagePath = RecordPath("LandResume"),
                    Version = "2.0.0",
                    State = PackageModuleOperationState.Executing,
                    At = DateTimeOffset.UtcNow.AddMinutes(-1),
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                }),
            },
        };
        await Create(interrupted, ct);

        var answered = await Live("LandResume", r => r.FactOutcomes?.ContainsKey(key) == true, ct);
        answered.FactOutcomes![key].Applied.Should().BeTrue(answered.FactOutcomes[key].Detail);
        answered.ModuleOperations.Should().BeNull("the resumed operation left the queue");
        var entry = await Entry(module, ct);
        entry.Should().NotBeNull();
        entry!.Enabled.Should().BeTrue();
        entry.Version.Should().Be("2.0.0");
        // The staged bytes are the WRITER's to drop, after its durable barrier — never the owner's
        // after an in-memory commit (#7974 review). This writer died, so they stay.
        Directory.Exists(Landing.StagedLandingDirectory(key)).Should().BeTrue("the owner never drops staged bytes itself");
    }

    /// <summary>
    /// (3) A fresh package with NO install record still lands — the boot's modules-first wave lands
    /// before any record exists — and no record is created by it. Negative control, in the test: a
    /// fresh-path landing of a module that was UNINSTALLED is refused, so "lands" is the decision and
    /// not a path that lands everything.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AFreshPackageWithNoRecord_StillLands()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Fresh.Module";
        var verdict = await Land(module, "1.0.0", RecordPath("LandFresh")).Timeout(TestTimeouts.Convergence).Await(ct);
        verdict.Applied.Should().BeTrue(verdict.Detail);
        (await Entry(module, ct))!.Enabled.Should().BeTrue();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        (await storage.Read(RecordPath("LandFresh"), Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull("a landing never creates an install record");

        // Negative control: uninstalled, then a fresh-path landing — refused.
        await Landing.RemoveModule(module).Timeout(TestTimeouts.Convergence).Await(ct);
        var refused = await Land(module, "1.1.0", RecordPath("LandFresh")).Timeout(TestTimeouts.Convergence).Await(ct);
        refused.Applied.Should().BeFalse();
        refused.Detail.Should().Contain("uninstalled after this landing was planned");
    }

    /// <summary>
    /// (4) Two concurrent landings of one package SERIALIZE at the owner: while the first executes
    /// (parked in its recording window) the second is QUEUED on the record, never executing beside it;
    /// released, both land, in the order stated. Negative control (measured): with the owner promoting
    /// every queued operation at once, the second is executing beside the first and this is red.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TwoConcurrentLandings_Serialize()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Concurrent.Module";
        await WriteRecord("LandTwo", module, ct);
        var executing = new List<int>();
        using var watch = Access.RunAsSystem(() => Mesh.GetMeshNodeStream(RecordPath("LandTwo")))
            .Select(n => n.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)?.ModuleOperations?
                .Count(o => o.Value.State == PackageModuleOperationState.Executing) ?? 0)
            .Subscribe(count => { lock (executing) executing.Add(count); });

        parkFirst = 1;
        try
        {
            var first = Land(module, "1.0.0", RecordPath("LandTwo")).Replay();
            using var firstConnection = first.Connect();
            SpinWait.SpinUntil(() => parked == 1, TestTimeouts.Convergence).Should().BeTrue("the first landing reaches its recording window");
            var second = Land(module, "2.0.0", RecordPath("LandTwo")).Replay();
            using var secondConnection = second.Connect();

            var both = await Live("LandTwo", r => r.ModuleOperations is { Count: 2 } ops
                && ops.Count(o => o.Value.State == PackageModuleOperationState.Executing) == 1
                && ops.Count(o => o.Value.State == PackageModuleOperationState.Queued) == 1, ct);
            both.ModuleOperations!.Values.Single(o => o.State == PackageModuleOperationState.Queued).Version
                .Should().Be("2.0.0", "the second landing waits behind the first");

            release = 1;
            (await first.Timeout(TestTimeouts.Convergence).Await(ct)).Applied.Should().BeTrue();
            (await second.Timeout(TestTimeouts.Convergence).Await(ct)).Applied.Should().BeTrue();
        }
        finally
        {
            release = 1;
        }
        (await Entry(module, ct))!.Version.Should().Be("2.0.0", "the landing stated second landed last");
        lock (executing)
            executing.Max().Should().Be(1, "never two module operations executing on one record");
    }

    /// <summary>
    /// (5) Only an AUTHORITATIVE install record executes module operations, and only on ITS OWN module
    /// (#7974 review): a Package node outside <c>Plugins/{id}</c> carrying a persisted executing removal
    /// moves nothing, and a real install record carrying an executing removal of ANOTHER package's
    /// module answers it refused. The landed module stays enabled. Negative control (measured): with
    /// the path check and the execution-time check removed, the module is disabled.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task APersistedExecutingOperation_NeverBypassesTheOwnersAuthority()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Victim.Module";
        await Landing.LandModule(module, [(module + ".dll", RealAssemblyBytes)], version: "1.0.0")
            .Timeout(TestTimeouts.Convergence).Await(ct);

        // A Package node where no install record belongs, forging an executing removal.
        var evil = Record("Evil", module) with { Id = "EvilRecord", Namespace = "EvilSpace" };
        await Create(evil with
        {
            Content = evil.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)! with
            {
                // Forged whole: even the retirement the removal would need is claimed, so only the
                // node's place — not an install record — stops it.
                RetiringBy = new PackageRetirement("EvilSpace/req", "forged", DateTimeOffset.UtcNow),
                ModuleOperations = ImmutableDictionary<string, PackageModuleOperation>.Empty
                    .Add("module-remove-evil", ExecutingRemove(module, "EvilSpace/req")),
            },
        }, ct);
        await Access.RunAsSystem(() => Mesh.GetMeshNodeStream("EvilSpace/EvilRecord")).Take(1)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        // A real install record of ANOTHER package, forging an executing removal of this module.
        var other = Record("AuthOther", "Acme.Other.Module");
        await Create(other with
        {
            Content = other.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)! with
            {
                ModuleOperations = ImmutableDictionary<string, PackageModuleOperation>.Empty
                    .Add("module-remove-forged", ExecutingRemove(module, "Admin/_PackageUninstall/req-forged")),
            },
        }, ct);
        var answered = await Live("AuthOther", r => r.FactOutcomes?.ContainsKey("module-remove-forged") == true, ct);
        answered.FactOutcomes!["module-remove-forged"].Applied.Should().BeFalse();
        answered.FactOutcomes["module-remove-forged"].Detail.Should().Contain("is not this package's module");

        (await Entry(module, ct))!.Enabled.Should().BeTrue("neither forged removal moved the shared module volume");
    }

    /// <summary>
    /// (6) A landing whose install record EXISTED when the adoption began, and is gone when the bytes
    /// arrive, has lost its owner and is REFUSED — even for a module that never landed, which leaves no
    /// tombstone to refuse it (#7974 review). Negative control, in the test: the same landing for a
    /// package that never had a record lands.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ALandingThatLostItsRecordWhileTheBytesTravelled_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.LostOwner.Module";
        await WriteRecord("LandLost", module, ct);
        // The adoption begins (record present), the uninstall removes the record (the module never
        // landed: no tombstone), then the bytes arrive.
        (await PackageInstaller.RemoveInstalledRecord(Mesh, "LandLost", NullLogger.Instance).Take(1)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeTrue();

        var refused = await Land(module, "1.0.0", RecordPath("LandLost"), recordAtPlan: true)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        refused.Applied.Should().BeFalse();
        refused.Detail.Should().Contain("existed when this adoption began");
        (await Entry(module, ct)).Should().BeNull("nothing was landed");

        // Negative control: a package that never had a record — the fresh path — lands.
        (await Land(module, "1.0.0", RecordPath("LandLost"), recordAtPlan: false)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Applied.Should().BeTrue();
    }

    /// <summary>
    /// (7) A PERSON's re-install that the owner authorized lands even after phase 1 wrote the
    /// tombstone (the retirement was not yet removing the record): the owner clears the retirement and
    /// carries the landing out over the tombstone, so the person keeps an install record WITH its module
    /// and the uninstall stops (#7974 review). Negative control (measured): with the owner's
    /// re-install authorization ignored at execution, the tombstone refuses the person's landing.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task APersonsReinstallDuringTheRetirement_LandsOverTheTombstone()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Reinstall.Module";
        const string request = "Admin/_PackageUninstall/req-reinstall";
        await WriteRecord("LandAgain", module, ct);
        (await Land(module, "1.0.0", RecordPath("LandAgain")).Timeout(TestTimeouts.Convergence).Await(ct)).Applied.Should().BeTrue();
        (await InstallRecordGate.BeginRetirement(Mesh, "LandAgain", request, "test", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull();
        (await ModuleLandingOwner.Remove(Mesh, "LandAgain", module, request, NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be("landed generation disabled");

        var person = await Land(module, "1.1.0", RecordPath("LandAgain"), PackageRecordWriteIntent.Person, recordAtPlan: true)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        person.Applied.Should().BeTrue(person.Detail);
        var entry = await Entry(module, ct);
        entry!.Enabled.Should().BeTrue("the person's re-install is landed over the tombstone");
        entry.Version.Should().Be("1.1.0");
        (await Live("LandAgain", r => r.RetiringBy is null, ct)).RetiringBy.Should().BeNull("the person's landing cleared the retirement");
        (await InstallRecordGate.MarkRemoving(Mesh, "LandAgain", request, NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().Contain("no longer holds", "the displaced uninstall stops and keeps the record");
    }

    // ── Through the bundle funnel (round 2 of the #7974 review) ────────────────────────────

    private PluginBundleClient Bundles => new(Mesh, FakeRegistry.Url, "mwi_test");

    /// <summary>A real module bundle: the module's entry assembly (real bytes — the landing link
    /// probes them) under the module lane, version declared to match the index.</summary>
    private static byte[] ModuleBundle(string plugin, string module, string version)
    {
        var entries = new[]
        {
            new NuGetPackageWriter.Entry(NuGetPackageWriter.ModuleEntryPathFor(module + ".dll"),
                () => new MemoryStream(RealAssemblyBytes)),
        };
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin,
            version,
            frameworkMvid = "33f2efb8aaaabbbbccccddddeeeeffff",
            module = new { assemblyName = module, assemblies = new[] { module + ".dll" }, version },
        });
        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(buffer, new MeshWeaver.Plugin.Packaging.PluginManifest(plugin, "MeshWeaver.Plugin." + plugin, version, plugin, null, []),
            "3.0.0", entries, manifestJson);
        return buffer.ToArray();
    }

    /// <summary>
    /// (8) The record's existence is read at the adoption's START — before the plan's index read: an
    /// uninstall that removes the record while the INDEX response is held (the module never landed, so
    /// no tombstone exists) leaves the landing refused, never a fresh package's (#7974 review, round
    /// 2). Negative control (measured): with the existence read back at execution (after the plan),
    /// the adoption lands the module.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARecordRemovedWhileTheIndexIsHeld_TheLandingIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.IndexHeld.Module";
        await WriteRecord("LandIdx", module, ct);
        registry.Serve("LandIdx", "1.0.0", module, ModuleBundle("LandIdx", module, "1.0.0"));
        registry.HoldIndex = 1;
        try
        {
            var adopt = Bundles.AdoptModuleOutcome("LandIdx", module, RecordPath("LandIdx")).Replay();
            using var connection = adopt.Connect();
            SpinWait.SpinUntil(() => registry.IndexEntered == 1, TestTimeouts.Convergence)
                .Should().BeTrue("the adoption's plan is waiting on the index");
            (await PackageInstaller.RemoveInstalledRecord(Mesh, "LandIdx", NullLogger.Instance).Take(1)
                .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeTrue();
            registry.HoldIndex = 0;

            var outcome = await adopt.Timeout(TestTimeouts.Convergence).Await(ct);
            outcome.FilesLanded.Should().Be(0);
            (outcome.Failure ?? "").Should().Contain("existed when this adoption began");
        }
        finally
        {
            registry.HoldIndex = 0;
        }
        (await Entry(module, ct)).Should().BeNull("nothing was landed");
    }

    /// <summary>
    /// (9) A PERSON's catalog re-install, through the bundle funnel, lands a module phase 1 already
    /// disabled: the plan reads the disabled entry as never landed for a person's install, and the
    /// owner carries it out over the tombstone (#7974 review, round 2). Negative control, in the test:
    /// the same adoption with no person intent is <see cref="ModuleUpdateAction.SkipUninstalled"/>.
    /// Negative control (measured): with the plan ignoring the person's intent, the re-install is
    /// skipped and the module stays disabled.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task APersonsCatalogReinstall_ThroughTheBundleFunnel_LandsTheDisabledModule()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Funnel.Module";
        const string request = "Admin/_PackageUninstall/req-funnel";
        await WriteRecord("LandFunnel", module, ct);
        registry.Serve("LandFunnel", "1.0.0", module, ModuleBundle("LandFunnel", module, "1.0.0"));
        var first = await Bundles.AdoptModuleOutcome("LandFunnel", module, RecordPath("LandFunnel"))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        first.FilesLanded.Should().BeGreaterThan(0, first.Failure ?? "");
        (await InstallRecordGate.BeginRetirement(Mesh, "LandFunnel", request, "test", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull();
        (await ModuleLandingOwner.Remove(Mesh, "LandFunnel", module, request, NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be("landed generation disabled");

        var unattended = await Bundles.AdoptModuleOutcome("LandFunnel", module, RecordPath("LandFunnel"))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        unattended.Verdict!.Action.Should().Be(ModuleUpdateAction.SkipUninstalled, "no person asked — the uninstall holds");

        var person = await Bundles.AdoptModuleOutcomeAs("LandFunnel", module, RecordPath("LandFunnel"),
                unattended: false, policyDecline: null, intent: PackageRecordWriteIntent.Person)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        person.FilesLanded.Should().BeGreaterThan(0, person.Failure ?? person.Verdict?.Reason ?? "");
        (await Entry(module, ct))!.Enabled.Should().BeTrue("the person's re-install landed the module again");
        (await Live("LandFunnel", r => r.RetiringBy is null, ct)).RetiringBy.Should().BeNull();
    }

    /// <summary>
    /// (10) Subscribing TWICE to one removal submits two operations, each answered (#7974 review,
    /// round 2). Negative control (measured): with the key minted once at call time, the second
    /// subscription's operation is promoted but never executed — the owner runs a key once per
    /// activation — and this times out red.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task SubscribingTwiceToARemoval_SubmitsTwoOperations()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Twice.Module";
        const string request = "Admin/_PackageUninstall/req-twice";
        await WriteRecord("LandTwice", module, ct);
        (await Land(module, "1.0.0", RecordPath("LandTwice")).Timeout(TestTimeouts.Convergence).Await(ct)).Applied.Should().BeTrue();
        (await InstallRecordGate.BeginRetirement(Mesh, "LandTwice", request, "test", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull();

        var remove = ModuleLandingOwner.Remove(Mesh, "LandTwice", module, request, NullLogger.Instance);
        (await remove.Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be("landed generation disabled");
        (await remove.Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be("landed generation disabled",
            "a second subscription is a second operation, answered like the first");
        (await Live("LandTwice", r => r.ModuleOperations is null, ct)).ModuleOperations.Should().BeNull("nothing is left executing");
    }

    /// <summary>
    /// (11) An adoption that names NO record path belongs to the canonical record <c>Plugins/{id}</c>
    /// anyway (#7974 review, round 3): with that record being retired, the pathless adoption is refused
    /// by its owner, never landed around it. Negative control (measured): with no default path, the
    /// adoption takes the pathless branch and lands the module.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task APathlessAdoption_GoesThroughTheCanonicalRecordsOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Pathless.Module";
        await WriteRecord("LandNoPath", module, ct);
        (await InstallRecordGate.BeginRetirement(Mesh, "LandNoPath", "Admin/_PackageUninstall/req-nopath", "test", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull();
        registry.Serve("LandNoPath", "1.0.0", module, ModuleBundle("LandNoPath", module, "1.0.0"));

        var outcome = await Bundles.AdoptModuleOutcome("LandNoPath", module).Timeout(TestTimeouts.Convergence).Await(ct);
        outcome.FilesLanded.Should().Be(0);
        (outcome.Failure ?? "").Should().Contain("being retired by Admin/_PackageUninstall/req-nopath");
        (await Entry(module, ct)).Should().BeNull("nothing landed around the record's owner");
    }

    /// <summary>
    /// (12) A person's install while the uninstall's REMOVAL is executing — parked on the shared
    /// landing lane behind another module — is refused at the owner, so it can never clear the
    /// retirement, keep the record and then lose the module to the removal (#7974 review, round 3).
    /// Once the removal finishes, the uninstall proceeds to remove the record. Negative control
    /// (measured): with the owner's check removed, the person's stamp is applied and the uninstall is
    /// displaced with the module disabled.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task APersonsInstallWhileTheRemovalExecutes_IsRefused_TheUninstallFinishes()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Race.Module";
        const string request = "Admin/_PackageUninstall/req-race";
        await WriteRecord("LandRace", module, ct);
        (await Land(module, "1.0.0", RecordPath("LandRace")).Timeout(TestTimeouts.Convergence).Await(ct)).Applied.Should().BeTrue();
        (await InstallRecordGate.BeginRetirement(Mesh, "LandRace", request, "test", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull();

        parkFirst = 1;
        try
        {
            // Another module's landing holds the shared (cap-1) landing lane.
            var other = Landing.LandModule("Acme.Other.Lane", [("Acme.Other.Lane.dll", RealAssemblyBytes)], version: "1.0.0").Replay();
            using var otherConnection = other.Connect();
            SpinWait.SpinUntil(() => parked == 1, TestTimeouts.Convergence).Should().BeTrue("the shared lane is parked");

            var remove = ModuleLandingOwner.Remove(Mesh, "LandRace", module, request, NullLogger.Instance).Replay();
            using var removeConnection = remove.Connect();
            await Live("LandRace", r => r.ModuleOperations?.Values.Any(o =>
                o.Kind == PackageModuleOperationKind.Remove && o.State == PackageModuleOperationState.Executing) == true, ct);

            var person = await InstallRecordGate.Write(Mesh, Record("LandRace", module), PackageRecordWriteIntent.Person, NullLogger.Instance)
                .Select(_ => (Exception?)null)
                .Catch((Exception ex) => Observable.Return<Exception?>(ex))
                .Timeout(TestTimeouts.Convergence).Await(ct);
            person.Should().BeOfType<PackageRecordWriteRefusedException>()
                .Which.Reason.Should().Contain("removing this package's module now");

            release = 1;
            (await remove.Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be("landed generation disabled");
            await other.Timeout(TestTimeouts.Convergence).Await(ct);
        }
        finally
        {
            release = 1;
        }
        (await InstallRecordGate.MarkRemoving(Mesh, "LandRace", request, NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull("the uninstall still holds the retirement and proceeds");
    }

    /// <summary>
    /// (13) An uninstall that timed out and RELEASED its retirement (End) leaves its removal executing;
    /// a person's install meanwhile is still refused — the guard is any removal in flight, not the
    /// retirement holder's (#7974 review, round 4). Once the removal finishes, the person's install is
    /// applied. Negative control (measured): with the guard bound to the retirement holder, the
    /// person's stamp is applied while the removal is still in flight.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AReleasedUninstallsRemovalInFlight_StillHoldsOffAPersonsInstall()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Released.Module";
        const string request = "Admin/_PackageUninstall/req-released";
        await WriteRecord("LandReleased", module, ct);
        (await Land(module, "1.0.0", RecordPath("LandReleased")).Timeout(TestTimeouts.Convergence).Await(ct)).Applied.Should().BeTrue();
        (await InstallRecordGate.BeginRetirement(Mesh, "LandReleased", request, "test", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull();

        IObservable<Exception?> Person() =>
            InstallRecordGate.Write(Mesh, Record("LandReleased", module), PackageRecordWriteIntent.Person, NullLogger.Instance)
                .Select(_ => (Exception?)null)
                .Catch((Exception ex) => Observable.Return<Exception?>(ex));

        parkRemoval = 1;
        try
        {
            var remove = ModuleLandingOwner.Remove(Mesh, "LandReleased", module, request, NullLogger.Instance).Replay();
            using var connection = remove.Connect();
            SpinWait.SpinUntil(() => removalParked == 1, TestTimeouts.Convergence).Should().BeTrue("the removal is in flight");
            // The uninstall's writer gave up and released the retirement; its removal runs on.
            (await InstallRecordGate.EndRetirement(Mesh, "LandReleased", request, NullLogger.Instance)
                .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull();

            var refused = await Person().Timeout(TestTimeouts.Convergence).Await(ct);
            refused.Should().BeOfType<PackageRecordWriteRefusedException>()
                .Which.Reason.Should().Contain("removing this package's module now");

            releaseRemoval = 1;
            (await remove.Timeout(TestTimeouts.Convergence).Await(ct)).Should().Be("landed generation disabled");
        }
        finally
        {
            releaseRemoval = 1;
        }
        (await Person().Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull("with no removal in flight the person's install is applied");
    }

    /// <summary>
    /// (14) An uninstall that starts while the module's FIRST landing is executing at the owner — so
    /// the activation has no entry yet — still removes the module: the removal is queued behind the
    /// landing and the owner decides once the landing finished. Real uninstall executor (#7974 review,
    /// round 4). Negative control (measured): with phase 1 skipping the removal when it reads no entry,
    /// the uninstall completes with the module enabled.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnUninstallDuringTheFirstLanding_RemovesTheModuleAfterIt()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.FirstLanding.Module";
        await WriteRecord("LandFirst", module, ct);
        await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(
                MeshNode.FromPath("LandFirst") with { NodeType = "Markdown", Name = "LandFirst", State = MeshNodeState.Active }))
            .Timeout(TestTimeouts.Convergence).Await(ct);

        parkFirst = 1;
        PackageUninstallRequest done;
        try
        {
            var first = Land(module, "1.0.0", RecordPath("LandFirst")).Replay();
            using var landing = first.Connect();
            SpinWait.SpinUntil(() => parked == 1, TestTimeouts.Convergence).Should().BeTrue("the first landing is executing");
            (await Entry(module, ct)).Should().BeNull("precondition: the activation has no entry while the first landing runs");

            var ticket = await PackageUninstall.Request(Mesh, new PackageUninstallRequest { Package = "LandFirst", Reason = "test", RequestedBy = "someone" })
                .Timeout(TestTimeouts.Convergence).Await(ct);
            var uninstall = Access.RunAsSystem(() => Mesh.GetMeshNodeStream(ticket.Path!))
                .Select(n => n.ContentAs<PackageUninstallRequest>(Mesh.JsonSerializerOptions))
                .OfType<PackageUninstallRequest>()
                .Where(r => r.Status == PackageUninstallStatus.AwaitingConfirmation || PackageUninstallStatus.IsTerminal(r.Status))
                .Take(1).Replay();
            using var uninstallConnection = uninstall.Connect();
            await Live("LandFirst", r => r.ModuleOperations?.Values.Any(o =>
                o.Kind == PackageModuleOperationKind.Remove && o.State == PackageModuleOperationState.Queued) == true, ct);

            release = 1;
            (await first.Timeout(TestTimeouts.Convergence).Await(ct)).Applied.Should().BeTrue();
            done = await uninstall.Timeout(TestTimeouts.Convergence).Await(ct);
        }
        finally
        {
            release = 1;
        }
        done.Status.Should().Be(PackageUninstallStatus.AwaitingConfirmation, done.Failure ?? done.ModuleOutcome ?? "");
        done.ModuleDisabled.Should().BeTrue(done.ModuleOutcome ?? "");
        (await Entry(module, ct))!.Enabled.Should().BeFalse("the module landed first, then the uninstall removed it");
    }

    /// <summary>
    /// (15) A FAILED ANSWER WRITE does not latch the queue (#7974 review, round 5): the owner's
    /// answer update faults once, the operation stays Executing in storage, and the owner — still
    /// alive, never recycled — executes it again and answers it; a later submission is answered too.
    /// Negative control, in the test: the seam fired exactly once, so the answer really failed.
    /// Negative control (measured): with the recovery removed (the key kept, the watcher's source
    /// not faulted), the first landing is never answered and this times out red.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AFailedAnswerWrite_IsExecutedAgain_TheQueueDoesNotLatch()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Unanswered.Module";
        await WriteRecord("LandUnanswered", module, ct);
        Interlocked.Exchange(ref failAnswerOnce, 1);

        var first = await Land(module, "1.0.0", RecordPath("LandUnanswered")).Timeout(TestTimeouts.Convergence).Await(ct);
        Volatile.Read(ref answerFailures).Should().Be(1, "the owner's first answer write failed");
        first.Applied.Should().BeTrue(first.Detail);

        var second = await Land(module, "1.1.0", RecordPath("LandUnanswered")).Timeout(TestTimeouts.Convergence).Await(ct);
        second.Applied.Should().BeTrue("a later submission is not queued behind a latched operation: " + second.Detail);
        (await Live("LandUnanswered", r => r.ModuleOperations is null, ct)).ModuleOperations.Should().BeNull("nothing is left executing");
        (await Entry(module, ct))!.Version.Should().Be("1.1.0");
    }

    /// <summary>
    /// (16) A TRANSIENT fault while the owner lands stays RETRYABLE (#7974 review, round 5): an
    /// <see cref="IOException"/> on the volume is answered not-applied WITH
    /// <see cref="PackageRecordFactOutcome.Transient"/>, which the bundle funnel carries to
    /// <see cref="ModuleAdoptOutcome.Transient"/> — so a reload records Faulted, re-armed, not a
    /// terminal Failed. Negative control, in the test: a DECIDED refusal (the package is being retired)
    /// through the same funnel is not transient. Negative control (measured): with the classification
    /// not carried, the I/O fault reads Transient == false.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ATransientLandingFault_StaysRetryable_ARefusalDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Flaky.Module";
        await WriteRecord("LandFlaky", module, ct);
        registry.Serve("LandFlaky", "1.0.0", module, ModuleBundle("LandFlaky", module, "1.0.0"));
        Interlocked.Exchange(ref failRecordingOnce, 1);

        var faulted = await Bundles.AdoptModuleOutcome("LandFlaky", module, RecordPath("LandFlaky"))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        Volatile.Read(ref failRecordingOnce).Should().Be(0, "the landing met the I/O fault");
        faulted.FilesLanded.Should().Be(0);
        (faulted.Failure ?? "").Should().Contain("faulted");
        faulted.Transient.Should().BeTrue("an I/O fault on the volume may clear on its own: " + faulted.Failure);

        const string retired = "Acme.Retired.Module";
        await WriteRecord("LandRetired", retired, ct);
        (await InstallRecordGate.BeginRetirement(Mesh, "LandRetired", "Admin/_PackageUninstall/req-retired", "test", NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct)).Should().BeNull();
        registry.Serve("LandRetired", "1.0.0", retired, ModuleBundle("LandRetired", retired, "1.0.0"));
        var refused = await Bundles.AdoptModuleOutcome("LandRetired", retired, RecordPath("LandRetired"))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        (refused.Failure ?? "").Should().Contain("being retired by");
        refused.Transient.Should().BeFalse("a decided refusal is final");
    }

    /// <summary>
    /// (17) An activation record the owner cannot read WHOLE at landing time is a FAULT, not a decided
    /// refusal (#7974 review, round 5): the read may succeed on the next pass, so the answer is
    /// not-applied AND transient, and a reload stays retryable. Negative control (measured): with the
    /// unreadable head answered as a refusal again, the verdict reads Transient == false.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnUnreadableModuleHead_IsATransientFault_NotARefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        const string module = "Acme.Unreadable.Module";
        await WriteRecord("LandUnreadable", module, ct);
        Directory.CreateDirectory(ModuleActivationSidecar.EntriesDirectory(moduleRoot));
        await File.WriteAllTextAsync(ModuleActivationSidecar.EntryPath(moduleRoot, module), "{ this is not an activation entry", ct);

        var verdict = await Land(module, "1.0.0", RecordPath("LandUnreadable")).Timeout(TestTimeouts.Convergence).Await(ct);
        verdict.Applied.Should().BeFalse("nothing lands over a head that could not be read");
        verdict.Detail.Should().Contain("could not be read whole");
        verdict.Transient.Should().BeTrue("an unreadable record may read whole on the next pass: " + verdict.Detail);
    }

    /// <summary>A registry serving one bundle; its index response can be HELD (a worker the test
    /// deliberately parks, released through a volatile flag under a bounded spin).</summary>
    private sealed class FakeRegistry : HttpMessageHandler
    {
        public const string Url = "https://registry.test";
        private volatile string index = "{\"bundles\":[]}";
        private volatile byte[] bundle = [];
        public volatile int HoldIndex;
        public volatile int IndexEntered;

        public void Serve(string plugin, string version, string module, byte[] bytes)
        {
            bundle = bytes;
            index = "{\"bundles\":[{\"plugin\":\"" + plugin + "\",\"version\":\"" + version
                + "\",\"url\":\"" + Url + PluginBundleClient.RoutePrefix + "/" + plugin
                + ".zip\",\"module\":\"" + module + "\"}]}";
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/index.json", StringComparison.Ordinal))
            {
                if (HoldIndex == 0)
                    return Task.FromResult(Index());
                IndexEntered = 1;
                return Task.Run(() =>
                {
                    SpinWait.SpinUntil(() => HoldIndex == 0, TestTimeouts.Convergence);
                    return Index();
                }, cancellationToken);
            }
            if (path.StartsWith(PluginBundleClient.RoutePrefix + "/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bundle) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private HttpResponseMessage Index() => new(HttpStatusCode.OK)
        {
            Content = new StringContent(index, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
