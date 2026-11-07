using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>Phase 1 of an uninstall reads the module activation WHOLE, and a resumed phase 1 counts an
/// already-disabled entry as done</b> (MeshWeaver.Plugins#5055, #7959 review). An unreadable activation
/// record used to read as "this module was never landed": phase 1 then removed the install record with
/// the generation still enabled. And a phase 1 resumed after its removal saw a disabled entry and lost
/// that the module had been disabled at all. Real mesh, real landing service on a temporary module
/// root, the real uninstall executor.
/// </summary>
public class PhaseOneReadsTheActivationWholeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Module = "MeshWeaver.RetireProbe";

    private readonly string moduleRoot = Path.Combine(Path.GetTempPath(), "mw-b1-" + Guid.NewGuid().ToString("N")[..8]);
    private ModuleLandingService? landing;

    /// <summary>Runs inside a removal between its tombstone and its projection — a test interrupts
    /// the removal there.</summary>
    private Action<string>? beforeProjecting;

    /// <summary>Whether this process reports <see cref="Module"/> as loaded.</summary>
    private volatile bool moduleLoaded;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddPluginCatalog()
        .ConfigureServices(services => services
            // Registered AFTER the catalog's own, so these are the instances resolved.
            .AddSingleton(_ => landing ??= new ModuleLandingService(null, EnsureDirectory(moduleRoot), null,
                beforeProjecting: name => beforeProjecting?.Invoke(name)))
            .AddSingleton(_ => new ModuleReloadAgent
            {
                LoadedNames = () => moduleLoaded
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Module }
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            })
            .AddSingleton<IModuleActivationRestart, FailingRestart>());

    /// <summary>A restart lane that is down — the step AFTER the removal that fails.</summary>
    private sealed class FailingRestart : IModuleActivationRestart
    {
        public const string Message = "the restart lane is down";

        public IObservable<ModuleRestartOutcome> RequestRestart(string reason) =>
            Observable.Throw<ModuleRestartOutcome>(new InvalidOperationException(Message));
    }

    private bool EntryFileEnabled()
    {
        var entry = JsonSerializer.Deserialize<ModuleActivationEntry>(
            File.ReadAllText(ModuleActivationSidecar.EntryPath(moduleRoot, Module)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(entry);
        return entry.Enabled;
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private ModuleLandingService Landing => Mesh.ServiceProvider.GetRequiredService<ModuleLandingService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private async Task Installed(string id, CancellationToken ct)
    {
        var record = MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{id}") with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = id,
            State = MeshNodeState.Active,
            Content = new PackageManifest { Id = id, Name = id, Source = "Plugins", Version = "1.0.0", TargetPartition = id, Module = Module },
        };
        await InstallRecordGate.Write(Mesh, record, PackageRecordWriteIntent.CreateAllowed, NullLogger.Instance)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(
                MeshNode.FromPath(id) with { NodeType = "Markdown", Name = id, State = MeshNodeState.Active }))
            .Timeout(TestTimeouts.Convergence).Await(ct);
    }

    private Task Land(CancellationToken ct) =>
        Landing.LandModule(Module, [(Module + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))], version: "1.0.0")
            .Timeout(TestTimeouts.Convergence).Await(ct);

    private async Task<PackageUninstallRequest> Uninstall(string id, CancellationToken ct)
    {
        var ticket = await PackageUninstall.Request(Mesh, new PackageUninstallRequest { Package = id, Reason = "test", RequestedBy = "someone" })
            .Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.NotNull(ticket.Path);
        return await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(ticket.Path!))
            .Select(n => n.ContentAs<PackageUninstallRequest>(Mesh.JsonSerializerOptions))
            .OfType<PackageUninstallRequest>()
            .Where(r => r.Status == PackageUninstallStatus.AwaitingConfirmation || PackageUninstallStatus.IsTerminal(r.Status))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
    }

    private Task<bool> RecordExists(string id, CancellationToken ct) =>
        Storage.Read($"{PackageInstaller.InstalledPartition}/{id}", Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .Select(n => n is not null).Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>
    /// An UNREADABLE activation record fails the retire and keeps the install record. Negative control
    /// (measured): with the unchecked read, the module reads as never landed and the record is removed.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnUnreadableActivationRecord_FailsTheRetire_AndKeepsTheInstallRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        await Installed("RetireUnreadable", ct);
        Directory.CreateDirectory(ModuleActivationSidecar.EntriesDirectory(moduleRoot));
        await File.WriteAllTextAsync(ModuleActivationSidecar.EntryPath(moduleRoot, Module), "{ this is not an activation entry", ct);

        var request = await Uninstall("RetireUnreadable", ct);

        Assert.Equal(PackageUninstallStatus.Failed, request.Status);
        Assert.Contains("could not be read whole", request.Failure ?? "");
        Assert.True(await RecordExists("RetireUnreadable", ct), "the install record is kept");
    }

    /// <summary>
    /// A phase 1 resumed after its removal — the entry already disabled — counts it as DONE:
    /// <see cref="PackageUninstallRequest.ModuleDisabled"/> is set, never lost. Negative control
    /// (measured): with a disabled entry read as "never landed", ModuleDisabled is false.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task APhaseOneResumedAfterItsRemoval_KeepsModuleDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await Land(ct);
        await Installed("RetireResumed", ct);
        // The interrupted first run: the removal happened, nothing after it did.
        await Landing.RemoveModule(Module).Timeout(TestTimeouts.Convergence).Await(ct);

        var request = await Uninstall("RetireResumed", ct);

        Assert.True(request.Status == PackageUninstallStatus.AwaitingConfirmation, request.Failure ?? request.Status);
        Assert.True(request.ModuleDisabled, request.ModuleOutcome ?? "");
        Assert.Contains("already disabled", request.ModuleOutcome ?? "");
    }

    /// <summary>
    /// A removal interrupted AFTER its tombstone and BEFORE its projection leaves the per-module file
    /// (what an older image reads) enabled while the checked activation reads disabled. Phase 1 still
    /// runs the removal, so the file ends disabled. Negative control (measured): with the disabled
    /// entry treated as done and the removal skipped, the file stays enabled.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARemovalInterruptedBeforeItsProjection_IsCompletedByPhaseOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await Land(ct);
        await Installed("RetireInterrupted", ct);
        var interrupt = 1;
        beforeProjecting = _ =>
        {
            if (Interlocked.Exchange(ref interrupt, 0) == 1)
                throw new IOException("interrupted between the tombstone and the projection");
        };
        var stopped = await Assert.ThrowsAnyAsync<Exception>(() =>
            Landing.RemoveModule(Module).Timeout(TestTimeouts.Convergence).Await(ct));
        Assert.Contains("interrupted between the tombstone and the projection", stopped.ToString());
        Assert.True(EntryFileEnabled(), "precondition: the projection was not written");
        Assert.DoesNotContain(
            (await Landing.GetCheckedActivation().Take(1).Timeout(TestTimeouts.Convergence).Await(ct)).Entries,
            e => e.Enabled && e.Name == Module);

        var request = await Uninstall("RetireInterrupted", ct);

        Assert.True(request.Status == PackageUninstallStatus.AwaitingConfirmation, request.Failure ?? request.Status);
        Assert.True(request.ModuleDisabled, request.ModuleOutcome ?? "");
        Assert.False(EntryFileEnabled(), "the per-module file older images read ends disabled");
    }

    /// <summary>
    /// A step AFTER the removal that fails (here the one restart) ends the request Failed — and it
    /// still records that the module IS disabled on disk. Negative control (measured): without the
    /// write right after the removal, ModuleDisabled is false.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AFailureAfterTheRemoval_StillRecordsModuleDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await Land(ct);
        await Installed("RetireThenFail", ct);
        moduleLoaded = true;

        var request = await Uninstall("RetireThenFail", ct);

        Assert.Equal(PackageUninstallStatus.Failed, request.Status);
        Assert.Contains(FailingRestart.Message, request.Failure ?? "");
        Assert.True(request.ModuleDisabled, request.Failure ?? "");
        Assert.False(EntryFileEnabled());
    }

    /// <summary>Control: a landed, enabled module is disabled by this run and recorded as such.</summary>
    [Fact(Timeout = 180_000)]
    public async Task ALandedModule_IsDisabledByThisRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await Land(ct);
        await Installed("RetireLanded", ct);

        var request = await Uninstall("RetireLanded", ct);

        Assert.True(request.Status == PackageUninstallStatus.AwaitingConfirmation, request.Failure ?? request.Status);
        Assert.True(request.ModuleDisabled);
        Assert.Contains("landed generation disabled", request.ModuleOutcome ?? "");
        Assert.DoesNotContain(
            (await Landing.GetCheckedActivation().Take(1).Timeout(TestTimeouts.Convergence).Await(ct)).Entries,
            e => e.Enabled && e.Name == Module);
    }
}
