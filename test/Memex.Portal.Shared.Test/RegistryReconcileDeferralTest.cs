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
/// 🚨 <b>A registry down for longer than the boot budget must DEFER the reconcile, not drop it
/// (Systemorph/MeshWeaver#2888).</b>
///
/// <para>The 2026-08-31 fix taught <see cref="RegistryUpdateReconciler"/> to re-ask a 503 within
/// its startup budget. The same day a registry stayed down past that budget and the pod gave up
/// with one Error line — no durable record, no notification, and no path that would ever run the
/// skipped reconcile short of a restart (the log line's "a human opening the catalog page" only
/// RENDERS the feed). This fixture pins the two halves that close that gap, against the REAL
/// reconciler, the REAL <see cref="RegistryPackageSource"/> HTTP path and a registry that answers
/// exactly as the incident's did:</para>
/// <list type="number">
///   <item>Exhausting the budget records the registry as <see cref="RegistryReconcileEntry.Pending"/>
///   on the ledger node the reconciler owns and raises ONE <c>Admin</c>-anchored notification
///   naming the registry and its last answer.</item>
///   <item>The next successful feed read — made through the same class every catalog surface
///   uses, with no involvement from the reconciler's caller — drains the marker: the reconcile
///   runs from THAT read's packages (no second round-trip), the ledger clears, and the
///   installation's outdated package gets its "Update available" reminder.</item>
/// </list>
/// </summary>
public class RegistryReconcileDeferralTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RegistryUrl = "http://registry.reconcile.test";
    private const string RegistryName = "Reconcile Test Registry";
    private const string PackageId = "ReconcileDeferralPkg";
    private const string InstalledModuleVersion = "mv-installed-7c1";
    private const string ServedModuleVersion = "mv-served-9e4";
    private const string Token = "mwi_reconcile_deferral_test";

    private readonly FakeRegistry registry = new();

    private System.Text.Json.JsonSerializerOptions Json => Mesh.JsonSerializerOptions;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            // The registry, as RegistryPackageSource reaches it: through the named HttpClient the
            // catalog wires. Nothing between the reconciler and the wire is replaced.
            .ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(new FakeRegistryClientFactory(registry)));

    [Fact]
    public async Task RegistryDownPastTheBudget_DefersTheReconcile_AndTheNextFeedReadDrainsIt()
    {
        await SeedInstallRecord();
        registry.Serve(HttpStatusCode.ServiceUnavailable, []);

        var reconciler = Mesh.ServiceProvider.GetRequiredService<RegistryUpdateReconciler>();
        var reference = new PluginRegistryReference { Name = RegistryName, Url = RegistryUrl, Token = Token };

        // ── 1. The boot reconcile, against a registry that 503s for the whole budget ────────────
        // Zero backoff: the budget is exhausted in milliseconds instead of ~26 s; the ATTEMPT count
        // below proves every retry in it was spent before the reconciler deferred.
        await reconciler.ReconcileRegistry(reference, _ => TimeSpan.Zero).Timeout(TestTimeouts.Convergence);
        // The whole boot budget is spent before the reconcile is deferred.
        Assert.Equal(RegistryUpdateReconciler.FeedReadRetries + 1, registry.Attempts);

        // ── 2a. The skipped reconcile is a durable fact on the node the reconciler owns ─────────
        var pending = await LedgerEntries()
            .Where(entries => entries.Any(e => e.Url == RegistryUrl && e.Pending))
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        var deferred = pending.Single(e => e.Url == RegistryUrl);
        Assert.Equal(RegistryName, deferred.Name);
        Assert.Equal(RegistryUpdateReconciler.FeedReadRetries + 1, deferred.Attempts);
        Assert.Contains("503", deferred.LastFault);
        Assert.NotNull(deferred.PendingSince);
        Assert.Null(deferred.LastReconciledAt);

        // ── 2b. Platform admins were told — ONE Admin-anchored bell pointing at the ledger ──────
        var notifications = await AdminNotifications()
            .Where(ns => ns.Any(n => n.TargetNodePath == RegistryUpdateReconciler.LedgerPath))
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        var notification = notifications.Single(n => n.TargetNodePath == RegistryUpdateReconciler.LedgerPath);
        Assert.Equal(NotificationType.System, notification.NotificationType);
        Assert.Contains(RegistryName, notification.Title);
        Assert.Contains(RegistryUrl, notification.Message);
        Assert.Contains("503", notification.Message);
        Assert.False(notification.IsRead);

        // ── 3. The registry recovers and somebody opens the catalog ─────────────────────────────
        // A plain feed read through the class every catalog surface uses — the reconciler is not
        // told anything by this caller. The served package carries a NEWER module version than the
        // install record, so a reconcile that actually runs has something observable to do.
        registry.Serve(HttpStatusCode.OK, [ServedManifest()]);
        var served = await new RegistryPackageSource(Mesh, RegistryUrl, Token)
            .ListPackages("HEAD").FirstAsync().Timeout(TestTimeouts.Convergence);
        Assert.Single(served, p => p.Id == PackageId);

        // ── 4. Drained: the ledger clears, and the reconcile ran ────────────────────────────────
        var drained = await LedgerEntries()
            .Where(entries => entries.Any(e => e.Url == RegistryUrl && !e.Pending && e.LastReconciledAt is not null))
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        Assert.Equal(RegistryReconcileEntry.ViaFeedRead, drained.Single(e => e.Url == RegistryUrl).LastReconciledVia);

        // The reconcile's own witness: the install record opted OUT of unattended updates, so a
        // changed module raises the "Update available" reminder — carrying the provenance that
        // names THIS registry.
        //
        // 🚨 It is read from the PLATFORM bell, not from the install record. Since
        // MeshWeaver#3156 a notification is delivered to its ADDRESSEE, and only a platform admin
        // can apply an update (the sibling refusal this same emitter raises says so in as many
        // words), so it is addressed to `Admin` instead of being filed under
        // `Plugins/{package}/_Notification` where every catalog reader saw it. The install record
        // stays the click target, which is what `TargetNodePath` is asserted on below.
        var reminder = await AdminNotifications()
            .Select(ns => ns.FirstOrDefault(n =>
                n.Title.StartsWith("Update available", StringComparison.Ordinal)))
            .Where(n => n is not null)
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        Assert.Contains($"Served by registry '{RegistryName}'", reminder!.Message);
        Assert.Equal($"{PackageInstaller.InstalledPartition}/{PackageId}", reminder.TargetNodePath);
        Assert.Equal(StartupErrorNotifier.AdminPartition, reminder.Recipient);

        // The drain reconciled from the packages the catalog read returned: the boot's attempts
        // plus that ONE read, and not a single extra round-trip to the registry.
        // The deferred reconcile must run from the read that drained it, never re-read the feed.
        Assert.Equal(RegistryUpdateReconciler.FeedReadRetries + 2, registry.Attempts);
    }

    private async Task SeedInstallRecord()
    {
        var manifest = new PackageManifest
        {
            Id = PackageId,
            Name = "Reconcile Deferral Package",
            Version = "1.0.0",
            ModuleVersion = InstalledModuleVersion,
            TargetPartition = PackageId,
            AutoUpdate = false,
        };
        var record = MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{PackageId}") with
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

    private static PackageManifest ServedManifest() => new()
    {
        Id = PackageId,
        Name = "Reconcile Deferral Package",
        Version = "1.0.1",
        ModuleVersion = ServedModuleVersion,
        TargetPartition = PackageId,
    };

    // The ledger, read LIVE through a children query (a query never storms on a node that does not
    // exist yet, and it re-emits on every write), flattened to its registry entries.
    private IObservable<IReadOnlyList<RegistryReconcileEntry>> LedgerEntries() =>
        Mesh.GetWorkspace()
            .GetQuery("ledger|reconcile",
                $"path:{PackageInstaller.InstalledPartition} scope:children nodeType:{RegistryUpdateReconciler.LedgerNodeType}")
            .Select(ns => (IReadOnlyList<RegistryReconcileEntry>)(ns ?? [])
                .Select(n => n.ContentAs<RegistryReconcileLedger>(Json))
                .Where(l => l is not null)
                .SelectMany(l => l!.Registries)
                .ToList());

    private IObservable<IReadOnlyList<Notification>> AdminNotifications() =>
        Mesh.GetWorkspace()
            .GetQuery("notif|Admin|reconcile",
                $"path:{StartupErrorNotifier.AdminPartition}/_Notification scope:children nodeType:Notification")
            .Select(ns => (IReadOnlyList<Notification>)(ns ?? [])
                .Select(n => n.ContentAs<Notification>(Json))
                .Where(n => n is not null)
                .Select(n => n!)
                .ToList());

    /// <summary>
    /// The registry as the incident saw it: every request answered with the status the test set —
    /// a 503 whose body names instance-key resolution as temporarily unavailable, exactly the
    /// answer memex got on 2026-08-31 — or, once recovered, the real list payload. Counts attempts.
    /// </summary>
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attempts);
            var current = status;
            var content = current == HttpStatusCode.OK
                ? body
                : "{\"error\":\"Instance-key resolution is temporarily unavailable — retry shortly. This is NOT a statement about your key or your grant.\"}";
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
