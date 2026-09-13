using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A module-published broadcast reconciles THAT package, within the process's lifetime —
/// not at its next boot (#3650, rule R3 of Doc/Architecture/ModuleAdoptionPolicy).</b>
///
/// <para>The registry POSTs a <see cref="ModulePublished"/> record to a consumer's webhook inbox
/// the moment a bundle lands on its shelf. This fixture is the consumer half, against the REAL
/// <see cref="RegistryUpdateReconciler"/> (started as the hosted service it is), the REAL inbox
/// store (<see cref="WebhookInbox.Deliver"/>, the exact path the anonymous endpoint takes) and a
/// registry that answers exactly what a real one would for a package whose bundle this consumer
/// has never landed:</para>
/// <list type="number">
///   <item>The boot pass runs against the fake registry and lands nothing — the feed declares no
///   module for either installed package, so the boot's module lane has nothing to do — and the
///   reconcile ledger exists from then on, which is what makes it an inbox target.</item>
///   <item>A broadcast for package A lands in the ledger's <c>_Inbox</c>. The reconciler drains
///   it: it reads A's OWN install record (which does declare a module), consults the registry's
///   bundle index, decides Land, and asks the registry for A's bundle — and never for B's. The
///   ledger records the pass as <c>broadcast</c>, and the delivery is deleted.</item>
///   <item>A broadcast from a registry this installation does not consume is dropped and deleted
///   without a single registry request.</item>
/// </list>
/// <para>The witness is the registry's own request log: which bundle was asked for is the one
/// thing a consumer cannot fake, and "asked for A, not B" is the whole claim.</para>
/// </summary>
public class RegistryUpdateReconcilerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RegistryUrl = "http://registry.broadcast.test";
    private const string RegistryName = "Broadcast Test Registry";
    private const string PackageA = "BroadcastPkgA";
    private const string PackageB = "BroadcastPkgB";
    private const string ModuleVersion = "mv-installed-4b2";
    private const string Token = "mwi_broadcast_test";
    private const string ServedFramework = "s1234567890abcdef1234567890abcdef";

    private readonly FakeRegistry registry = new();

    private JsonSerializerOptions Json => Mesh.JsonSerializerOptions;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            // The generic webhook inbox the portal registers (MemexConfiguration) — the WebhookEvent
            // node type a delivery is stored as. Same production registration, not a duplicate.
            .AddWebhookInbox()
            .ConfigureServices(s => s
                .AddSingleton<IHttpClientFactory>(new FakeRegistryClientFactory(registry))
                // The registry this installation consumes — what arms the reconciler's boot pass,
                // its inbox watch and its safety net (left at its default; it never ticks here).
                .AddSingleton(new PluginCatalogOptions
                {
                    Registries =
                    [
                        new PluginRegistryReference { Name = RegistryName, Url = RegistryUrl, Token = Token },
                    ],
                }));

    [Fact(Timeout = 240_000)]
    public async Task ABroadcastForOnePackage_ReconcilesThatPackage_AndOnlyThat()
    {
        await SeedInstallRecord(PackageA, "ModA");
        await SeedInstallRecord(PackageB, "ModB");

        // ── 1. The boot pass ran (the hosted service started with the mesh) ───────────────────
        await LedgerEntries()
            .Where(entries => entries.Any(e => e.Url == RegistryUrl && e.LastReconciledVia == RegistryReconcileEntry.ViaBoot))
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        Assert.Empty(registry.BundleDownloads);
        var feedReadsAfterBoot = registry.FeedReads;

        // ── 2. The registry tells this installation that A's module was published ────────────
        var delivered = await WebhookInbox.Deliver(
                Mesh, [new WebhookInbox.WebhookTarget(ModulePublished.InboxTarget)],
                ModulePublished.InboxTarget, "application/json", [],
                new ModulePublished
                {
                    Registry = RegistryUrl,
                    Package = PackageA,
                    Module = "ModA",
                    Version = "1.0.0",
                    FrameworkMvid = ServedFramework,
                    PublishedAt = DateTimeOffset.UtcNow,
                }.Serialize())
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        Assert.Equal(WebhookInbox.DeliveryStatus.Accepted, delivered.Status);

        // ── 3. The drain reconciled A: the registry was asked for A's bundle ──────────────────
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => registry.BundleDownloads)
            .Where(downloads => downloads.Contains(PackageA))
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        var drained = await LedgerEntries()
            .Where(entries => entries.Any(e => e.Url == RegistryUrl && e.LastReconciledVia == RegistryReconcileEntry.ViaBroadcast))
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        Assert.NotNull(drained.Single(e => e.Url == RegistryUrl).LastReconciledAt);

        // ── 4. …and only A. The broadcast is per package, and B was never asked about ─────────
        Assert.Equal([PackageA], registry.BundleDownloads);
        // A broadcast drain reads the bundle INDEX for its package — never the whole feed again.
        Assert.Equal(feedReadsAfterBoot, registry.FeedReads);

        // ── 5. The delivery is consumed ───────────────────────────────────────────────────────
        await InboxDeliveries()
            .Where(deliveries => deliveries.Count == 0)
            .FirstAsync().Timeout(TestTimeouts.Convergence);
    }

    [Fact(Timeout = 240_000)]
    public async Task ABroadcastFromAnUnknownRegistry_IsDroppedWithoutARequest()
    {
        await SeedInstallRecord(PackageA, "ModA");
        await LedgerEntries()
            .Where(entries => entries.Any(e => e.Url == RegistryUrl && e.LastReconciledVia == RegistryReconcileEntry.ViaBoot))
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        var requestsBefore = registry.Requests.Count;

        var delivered = await WebhookInbox.Deliver(
                Mesh, [new WebhookInbox.WebhookTarget(ModulePublished.InboxTarget)],
                ModulePublished.InboxTarget, "application/json", [],
                new ModulePublished
                {
                    Registry = "https://somebody-else.example",
                    Package = PackageA,
                    Version = "1.0.0",
                    PublishedAt = DateTimeOffset.UtcNow,
                }.Serialize())
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        Assert.Equal(WebhookInbox.DeliveryStatus.Accepted, delivered.Status);

        // Consumed — and nothing was asked of the registry this installation does consume.
        await InboxDeliveries()
            .Where(deliveries => deliveries.Count == 0)
            .FirstAsync().Timeout(TestTimeouts.Convergence);
        Assert.Empty(registry.BundleDownloads);
        Assert.Equal(requestsBefore, registry.Requests.Count);
    }

    /// <summary>The host-based match: a consumer configured with a trailing slash or another scheme
    /// still recognises its registry; a different host never does. Since #4094 the reader is the
    /// platform's ONE registry-host rule (<c>SelfUpdateOptions.HostOf</c>, shared with the
    /// self-updater's credential selection), so a value that names no http(s) host names no
    /// registry — two copies of an unreadable string no longer "match", and a URL carrying
    /// userinfo matches nothing rather than the host a human would not have read.</summary>
    [Theory]
    [InlineData("https://memex.meshweaver.cloud", "https://memex.meshweaver.cloud/", true)]
    [InlineData("https://memex.meshweaver.cloud", "http://memex.meshweaver.cloud", true)]
    [InlineData("https://MEMEX.meshweaver.cloud/", "https://memex.meshweaver.cloud", true)]
    [InlineData("https://memex.meshweaver.cloud:443", "https://memex.meshweaver.cloud", true)]
    [InlineData("https://memex.meshweaver.cloud", "https://memex.systemorph.com", false)]
    [InlineData("http://registry:8080", "http://registry:9090", false)]
    [InlineData("http://registry:8080", "http://registry", false)]
    [InlineData("not a url", "not a url/", false)]
    [InlineData("https://memex.meshweaver.cloud@evil.example.test", "https://evil.example.test", false)]
    public void SameRegistry_MatchesByHost(string configured, string announced, bool expected)
        => Assert.Equal(expected, RegistryUpdateReconciler.SameRegistry(configured, announced));

    // ── harness ───────────────────────────────────────────────────────────────

    private async Task SeedInstallRecord(string packageId, string module)
    {
        var manifest = new PackageManifest
        {
            Id = packageId,
            Name = packageId,
            Version = "1.0.0",
            ModuleVersion = ModuleVersion,
            TargetPartition = packageId,
            Module = module,
            AutoUpdate = true,
        };
        var record = MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{packageId}") with
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

    private IObservable<IReadOnlyList<RegistryReconcileEntry>> LedgerEntries() =>
        Mesh.GetWorkspace()
            .GetQuery("ledger|broadcast",
                $"path:{PackageInstaller.InstalledPartition} scope:children nodeType:{RegistryUpdateReconciler.LedgerNodeType}")
            .Select(ns => (IReadOnlyList<RegistryReconcileEntry>)(ns ?? [])
                .Select(n => n.ContentAs<RegistryReconcileLedger>(Json))
                .Where(l => l is not null)
                .SelectMany(l => l!.Registries)
                .ToList());

    private IObservable<IReadOnlyList<MeshNode>> InboxDeliveries() =>
        Mesh.GetWorkspace()
            .GetQuery("inbox|broadcast",
                $"path:{ModulePublished.InboxTarget}/{WebhookInbox.InboxContainer} scope:children nodeType:{WebhookInbox.NodeType}")
            .Select(ns => (IReadOnlyList<MeshNode>)(ns ?? []).ToList());

    /// <summary>
    /// The registry, as both the feed client and the bundle client reach it. The feed lists both
    /// packages at the installed content identity and declares NO module for either (so the boot's
    /// module lane is idle); the bundle index serves a module for each; a bundle download answers
    /// 404 — the "no prebuilt bundle" a consumer treats as a logged miss — and is RECORDED, which
    /// is the witness.
    /// </summary>
    private sealed class FakeRegistry : HttpMessageHandler
    {
        private ImmutableList<string> requests = ImmutableList<string>.Empty;
        private ImmutableList<string> downloads = ImmutableList<string>.Empty;

        public ImmutableList<string> Requests => requests;
        public ImmutableList<string> BundleDownloads => downloads;

        /// <summary>How many times the whole feed (<c>GET /api/plugins</c>) was read.</summary>
        public int FeedReads => requests.Count(r => r.StartsWith("GET /api/plugins?", StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            ImmutableInterlocked.Update(ref requests, r => r.Add($"{request.Method} {path}"));

            string? body = null;
            var status = HttpStatusCode.NotFound;
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/plugins?", StringComparison.Ordinal))
            {
                body = PluginRegistryPayloads.List([Served(PackageA), Served(PackageB)]);
                status = HttpStatusCode.OK;
            }
            else if (request.Method == HttpMethod.Get && path.StartsWith($"{PluginBundleClient.RoutePrefix}/index.json", StringComparison.Ordinal))
            {
                body = JsonSerializer.Serialize(new
                {
                    frameworkMvid = ServedFramework,
                    bundles = new[]
                    {
                        new { plugin = PackageA, version = "1.0.0", url = $"{RegistryUrl}{PluginBundleClient.RoutePrefix}/{PackageA}/1.0.0", module = "ModA", frameworkMvid = ServedFramework },
                        new { plugin = PackageB, version = "1.0.0", url = $"{RegistryUrl}{PluginBundleClient.RoutePrefix}/{PackageB}/1.0.0", module = "ModB", frameworkMvid = ServedFramework },
                    },
                }, PluginRegistryPayloads.Json);
                status = HttpStatusCode.OK;
            }
            else if (request.Method == HttpMethod.Get && path.StartsWith($"{PluginBundleClient.RoutePrefix}/", StringComparison.Ordinal))
            {
                var package = path[(PluginBundleClient.RoutePrefix.Length + 1)..].Split('/')[0];
                ImmutableInterlocked.Update(ref downloads, d => d.Add(Uri.UnescapeDataString(package)));
            }

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? "{\"error\":\"not found\"}", Encoding.UTF8, "application/json"),
            });
        }

        private static PackageManifest Served(string packageId) => new()
        {
            Id = packageId,
            Name = packageId,
            Version = "1.0.0",
            ModuleVersion = ModuleVersion,
            TargetPartition = packageId,
        };
    }

    private sealed class FakeRegistryClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
