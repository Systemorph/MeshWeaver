using System;
using System.IO;
using System.Reactive.Linq;
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

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddPluginCatalog()
        .ConfigureServices(services => services
            // Registered AFTER the catalog's own, so this is the instance resolved.
            .AddSingleton(_ => landing ??= new ModuleLandingService(baseDirectory: EnsureDirectory(moduleRoot))));

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
