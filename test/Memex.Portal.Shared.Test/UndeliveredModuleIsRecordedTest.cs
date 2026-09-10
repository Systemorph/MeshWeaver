using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A store-installed module the registry does NOT OFFER must be recorded as not delivered —
/// never left reading as installed and up to date (Systemorph/MeshWeaver.Plugins#1584).</b>
///
/// <para>Both lanes of <see cref="RegistryUpdateReconciler"/>'s reconcile iterate the packages the
/// REGISTRY SERVES and intersect them with this installation's install records. A package
/// installed here that the registry does not serve is therefore not "up to date" and not "failed"
/// — it is absent from both loops, and absence produced no log line, no ledger entry and no card
/// state anywhere.</para>
///
/// <para>Measured on memex 2026-09-10: the registry answered its feed with 45 manifests and
/// <c>Mail</c> was not among them — the package declares <c>tier: personal</c> while the instance's
/// catalog grant covers the baseline plan, so the registry's own maintenance task answered "the
/// registry does not offer a package 'Mail' to this instance". The CONTENT kept arriving through
/// the instance's own git source and the install record advanced to 1.5.0 an hour before the
/// measurement, while <c>MeshWeaver.Mail.MicrosoftGraph</c> went on loading an assembly written
/// eleven days earlier and every dashboard read "installed, up to date". An Executive Assistant
/// thread asked for the Teams tools that shipped in 1.5 and had none.</para>
///
/// <para>This fixture pins the mechanism against the REAL reconciler, the REAL
/// <see cref="RegistryPackageSource"/> HTTP path and a registry that answers exactly as memex's
/// did — a full, successful feed that simply does not list the package:</para>
/// <list type="number">
///   <item>The undelivered package is named on the ledger node the reconciler owns, carrying the
///   module whose bytes are not arriving and the content identity the record claims.</item>
///   <item>A package the registry DOES offer is not named — being served is what delivery is.</item>
///   <item>A CONTENT-ONLY installed package the registry does not offer is not named either: it
///   declares no module, so there are no module bytes to deliver and calling it undelivered would
///   be an alarm nobody can act on.</item>
///   <item>The consumer-wide verdict (<see cref="ModuleDelivery.NotDeliveredByAnyRegistry"/>) reads
///   the same answer off the ledger, with <see cref="ModuleDelivery.AnyRegistryDetermined"/> as its
///   denominator — so an empty result can be read as "nothing undelivered" instead of "nothing
///   known".</item>
/// </list>
/// </summary>
public class UndeliveredModuleIsRecordedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RegistryUrl = "http://registry.delivery.test";
    private const string RegistryName = "Delivery Test Registry";
    private const string Token = "mwi_undelivered_module_test";

    /// <summary>Installed here, declares a module, and the registry SERVES it — delivered.</summary>
    private const string ServedPackageId = "DeliveredPkg";

    /// <summary>Installed here, declares a module, and the registry does NOT serve it. The
    /// <c>Mail</c> shape: content moves, module bytes do not.</summary>
    private const string DeclinedPackageId = "DeclinedPkg";
    private const string DeclinedModule = "MeshWeaver.Delivery.Declined";
    private const string DeclinedModuleVersion = "mv-declined-1f5";

    /// <summary>Installed here, NOT served, and declares no module — nothing to deliver.</summary>
    private const string ContentOnlyPackageId = "ContentOnlyPkg";

    private readonly FakeRegistry registry = new();

    private System.Text.Json.JsonSerializerOptions Json => Mesh.JsonSerializerOptions;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            // The registry, as RegistryPackageSource reaches it: through the named HttpClient the
            // catalog wires. Nothing between the reconciler and the wire is replaced.
            .ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(new FakeRegistryClientFactory(registry)));

    [Fact]
    public async Task AFullFeedThatOmitsAnInstalledModulePackage_RecordsItAsNotDelivered()
    {
        await SeedInstallRecord(ServedPackageId, module: "MeshWeaver.Delivery.Served",
            moduleVersion: "mv-served-9e4");
        await SeedInstallRecord(DeclinedPackageId, module: DeclinedModule,
            moduleVersion: DeclinedModuleVersion);
        await SeedInstallRecord(ContentOnlyPackageId, module: null, moduleVersion: "mv-content-3b2");

        // 🚨 A SUCCESSFUL, COMPLETE feed that simply does not list the other two packages — the
        // entitlement decline as a consumer sees it. Nothing here is an error: the registry
        // answered in full, which is precisely what makes the absence evidence rather than noise.
        registry.Serve(HttpStatusCode.OK, [ServedManifest()]);

        var reconciler = Mesh.ServiceProvider.GetRequiredService<RegistryUpdateReconciler>();
        var reference = new PluginRegistryReference { Name = RegistryName, Url = RegistryUrl, Token = Token };
        await reconciler.ReconcileRegistry(reference, _ => TimeSpan.Zero).Timeout(TestTimeouts.Convergence);

        // ── The reconcile completed, and it recorded WHAT IT DOES NOT DELIVER ───────────────────
        var entries = await LedgerEntries()
            .Where(es => es.Any(e => e.Url == RegistryUrl && e.LastReconciledAt is not null))
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        var entry = entries.Single(e => e.Url == RegistryUrl);
        Assert.Equal(RegistryReconcileEntry.ViaBoot, entry.LastReconciledVia);

        // 🚨 null would mean "not determined". The feed read succeeded, so the question IS answered.
        Assert.NotNull(entry.UndeliveredModules);
        var undelivered = Assert.Single(entry.UndeliveredModules!);
        Assert.Equal(DeclinedPackageId, undelivered.PackageId);
        // The module whose bytes are not arriving, and the identity the record claims is installed
        // — the pair that makes "installed 1.5, running an eleven-day-old assembly" legible.
        Assert.Equal(DeclinedModule, undelivered.Module);
        Assert.Equal(DeclinedModuleVersion, undelivered.InstalledModuleVersion);

        // A package the registry serves is delivered; a content-only package has nothing to deliver.
        Assert.DoesNotContain(entry.UndeliveredModules!, u => u.PackageId == ServedPackageId);
        Assert.DoesNotContain(entry.UndeliveredModules!, u => u.PackageId == ContentOnlyPackageId);

        // ── The consumer-wide verdict reads the same answer, with its denominator ───────────────
        var ledger = new RegistryReconcileLedger { Registries = [.. entries] };
        Assert.True(ModuleDelivery.AnyRegistryDetermined(ledger));
        Assert.Equal(
            [DeclinedPackageId],
            ModuleDelivery.NotDeliveredByAnyRegistry(ledger).Select(u => u.PackageId));
    }

    private async Task SeedInstallRecord(string id, string? module, string moduleVersion)
    {
        var manifest = new PackageManifest
        {
            Id = id,
            Name = $"{id} display name",
            Version = "1.5.0",
            ModuleVersion = moduleVersion,
            Module = module,
            TargetPartition = id,
            AutoUpdate = false,
        };
        var record = MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{id}") with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = manifest.Name,
            State = MeshNodeState.Active,
            Content = manifest,
        };
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(record))
            .Timeout(TestTimeouts.Convergence);
    }

    /// <summary>The one package the registry offers — at the SAME module content identity the
    /// install record carries, so the content lane has nothing to say and the only thing this test
    /// observes is the delivery verdict.</summary>
    private static PackageManifest ServedManifest() => new()
    {
        Id = ServedPackageId,
        Name = $"{ServedPackageId} display name",
        Version = "1.5.0",
        ModuleVersion = "mv-served-9e4",
        Module = "MeshWeaver.Delivery.Served",
        TargetPartition = ServedPackageId,
    };

    // The ledger, read LIVE through a children query (a query never storms on a node that does not
    // exist yet, and it re-emits on every write), flattened to its registry entries.
    private IObservable<IReadOnlyList<RegistryReconcileEntry>> LedgerEntries() =>
        Mesh.GetWorkspace()
            .GetQuery("ledger|delivery",
                $"path:{PackageInstaller.InstalledPartition} scope:children nodeType:{RegistryUpdateReconciler.LedgerNodeType}")
            .Select(ns => (IReadOnlyList<RegistryReconcileEntry>)(ns ?? [])
                .Select(n => n.ContentAs<RegistryReconcileLedger>(Json))
                .Where(l => l is not null)
                .SelectMany(l => l!.Registries)
                .ToList());

    /// <summary>A registry that answers every request with the status and list payload the test
    /// set. The undelivered package is absent from that payload — that IS the decline.</summary>
    private sealed class FakeRegistry : HttpMessageHandler
    {
        private volatile HttpStatusCode status = HttpStatusCode.ServiceUnavailable;
        private volatile string body = "";
        private int attempts;

        public int Attempts => Volatile.Read(ref attempts);

        public void Serve(HttpStatusCode code, IReadOnlyList<PackageManifest> packages)
        {
            // Body before status: a reader that sees the new status sees the body it belongs to.
            body = PluginRegistryPayloads.List(packages);
            status = code;
        }

        // HttpMessageHandler's contract is Task-shaped; nothing here bridges an observable.
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attempts);
            var current = status;
            var content = current == HttpStatusCode.OK ? body : "{\"error\":\"unavailable\"}";
            return Task.FromResult(new HttpResponseMessage(current)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeRegistryClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
