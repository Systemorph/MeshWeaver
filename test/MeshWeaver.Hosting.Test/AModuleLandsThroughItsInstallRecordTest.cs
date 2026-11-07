using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
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
    private ModuleLandingService? landing;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false })
                // Registered AFTER the catalog's own, so this is the instance every hub resolves.
                .AddSingleton(_ => landing ??= new ModuleLandingService(null, moduleRoot, beforeRecording: _ =>
                {
                    if (parkFirst == 0 || Interlocked.Increment(ref entered) != 1)
                        return;
                    parked = 1;
                    SpinWait.SpinUntil(() => release == 1, TestTimeouts.Convergence);
                })));

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
}
