#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
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
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A bake published after its source landed is adopted when it is published</b>
/// (<see cref="HeldNodeTypeBundleRetry"/>, the registry reconcile's held-NodeType lane).
///
/// <para>The incident it pins (an enterprise client's test instance, 2026-10-18): the source of a
/// NodeType landed at 06:56Z, the bundle that named the type still carried the previous source and
/// was declined, and the type went into a delivery hold. The bake was published at 07:43Z, and
/// nothing asked for it again, because no component re-requests a held type. What is pinned: every
/// reconcile pass asks the registry's index for the packages of held types; it downloads a
/// package's bundle exactly when the index names a bundle this process has not tried; an unchanged
/// index costs no download; and a mesh with nothing held does not even read the index. The
/// witness is the fake registry's own request log, because what was asked is the one thing the
/// consumer cannot fake.</para>
/// </summary>
public class HeldNodeTypeBundleRetryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RegistryHost = "registry.held-retry.test";
    private const string RegistryUrl = "https://" + RegistryHost;
    private const string Token = "mwi_held_retry";
    private const string Package = "HeldPkg";
    private const string HeldType = Package + "/Submission";
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 7, 44, 0, TimeSpan.Zero);

    private readonly FakeIndex registry = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IHttpClientFactory>(new HostRoutingClientFactory(registry)));

    // ── the decision ──────────────────────────────────────────────────────────────────────────

    private static PluginBundleClient.BundleRef Bundle(string plugin, string version, string? artifact = null) =>
        new(plugin, version, $"{RegistryUrl}{PluginBundleClient.RoutePrefix}/{plugin}/{version}") { Artifact = artifact };

    [Fact]
    public void AHeldTypesPackage_IsRetried_WhenNothingWasTriedYet_OrTheAdvertisedBundleMoved()
    {
        var advertised = new[] { Bundle(Package, "1.2", "cr.test/plugins/reinsurance/heldpkg@sha256:" + new string('b', 64)) };

        var first = HeldNodeTypeBundleRetry.Plan([HeldType], advertised, _ => null, Now, HeldNodeTypeBundleRetry.ReattemptAfter);
        first.Should().ContainSingle();
        first[0].Package.Should().Be(Package);
        first[0].HeldTypes.Should().Equal(HeldType);

        var tried = new HeldNodeTypeBundleRetry.Attempt(HeldNodeTypeBundleRetry.BundleKeyOf(advertised[0]), Now.AddMinutes(-5));
        HeldNodeTypeBundleRetry.Plan([HeldType], advertised, _ => tried, Now, HeldNodeTypeBundleRetry.ReattemptAfter)
            .Should().BeEmpty("the index names exactly the bundle this process already tried");

        // The rebake: a new digest-addressed artifact for the same package and version.
        var rebaked = new[] { Bundle(Package, "1.2", "cr.test/plugins/reinsurance/heldpkg@sha256:" + new string('c', 64)) };
        var after = HeldNodeTypeBundleRetry.Plan([HeldType], rebaked, _ => tried, Now, HeldNodeTypeBundleRetry.ReattemptAfter);
        after.Should().ContainSingle("a rebake moves the artifact, and that is what the lane must see");
        after[0].Because.Should().Contain("now advertises");
    }

    [Fact]
    public void AnUnchangedBundle_IsRetriedAgain_OnlyOnceTheBoundHasPassed()
    {
        var advertised = new[] { Bundle(Package, "1.2") };
        var key = HeldNodeTypeBundleRetry.BundleKeyOf(advertised[0]);

        HeldNodeTypeBundleRetry.Plan([HeldType], advertised,
                _ => new HeldNodeTypeBundleRetry.Attempt(key, Now.AddHours(-1)), Now, TimeSpan.FromHours(6))
            .Should().BeEmpty();
        HeldNodeTypeBundleRetry.Plan([HeldType], advertised,
                _ => new HeldNodeTypeBundleRetry.Attempt(key, Now.AddHours(-7)), Now, TimeSpan.FromHours(6))
            .Should().ContainSingle("a registry may re-shelve new bytes under an unchanged reference");
    }

    [Fact]
    public void APackageTheIndexDoesNotAdvertise_OrNothingHeld_IsNeverRetried()
    {
        HeldNodeTypeBundleRetry.Plan([HeldType], [Bundle("Other", "1.0")], _ => null, Now, HeldNodeTypeBundleRetry.ReattemptAfter)
            .Should().BeEmpty("there is nothing to adopt for a package the registry does not serve");
        HeldNodeTypeBundleRetry.Plan([], [Bundle(Package, "1.0")], _ => null, Now, HeldNodeTypeBundleRetry.ReattemptAfter)
            .Should().BeEmpty("no hold, no retry");
        // Control: the same inputs with a hold DO plan, so the two empties above are about the inputs.
        HeldNodeTypeBundleRetry.Plan([HeldType], [Bundle(Package, "1.0")], _ => null, Now, HeldNodeTypeBundleRetry.ReattemptAfter)
            .Should().ContainSingle();
    }

    // ── the lane, against a registry ──────────────────────────────────────────────────────────

    private void Hold(params string[] typePaths)
    {
        var records = typePaths.Select(p => (p, (NodeTypeDefinition?)new NodeTypeDefinition
        {
            Configuration = "config => config",
            CompilationStatus = CompilationStatus.Ok,
            CompiledFrameworkVersion = PrebuiltAssemblySeeder.LiveFrameworkMvid,
            BuildProvenance = BuildProvenance.StaleAdopted,
            LatestAssemblyCollection = "local",
            LatestAssemblyPath = $"{p.Replace('/', '_')}/v16.dll",
            DeliveryHoldReason = "Serving the last build this mesh holds; the current source is waiting for a bundle.",
        })).ToArray();
        Mesh.ServiceProvider.GetRequiredService<NodeTypeBakeReportRegistry>().RecordLiveRecords(
            NodeTypeLiveRecordCensus.Of(records, PrebuiltAssemblySeeder.LiveFrameworkMvid, Now.AddHours(-2), Now));
    }

    /// <summary>The install record the lane requires before it adopts a package (Plugins/{id}).</summary>
    private Task Install(string packageId, CancellationToken ct)
    {
        var record = MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{packageId}") with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = packageId,
            State = MeshNodeState.Active,
            Content = new PackageManifest { Id = packageId, Name = packageId, Version = "1.0.0", TargetPartition = packageId },
        };
        return Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => NodeFactory.CreateOrUpdateNode(record))
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
    }

    /// <summary>
    /// The installed-package boundary: a NodeType held in a partition whose id matches a package the
    /// registry advertises but this mesh never installed is NOT adopted for — no index read, no
    /// download. The control installs the package, and the same hold is then adopted for.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AHeldTypeOfAnUninstalledPackage_IsNeverAdoptedFor()
    {
        var ct = TestContext.Current.CancellationToken;
        var lane = new HeldNodeTypeBundleRetry();
        Hold(HeldType);

        (await Pass(lane, ct)).Should().Be(0);
        registry.IndexReads.Should().Be(0, "an uninstalled package is not asked about");
        registry.Downloads.Should().BeEmpty();

        // Control: installed, the same hold is adopted for.
        await Install(Package, ct);
        await Pass(lane, ct);
        registry.Downloads.Should().Equal([Package]);
    }

    /// <summary>The index can advertise a bundle before its bytes are reachable: a 404 for them
    /// judged nothing, so the same reference is asked for again on the next pass.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ABundleAnsweredNotFound_IsAskedForAgain_OnTheNextPass()
    {
        var ct = TestContext.Current.CancellationToken;
        var lane = new HeldNodeTypeBundleRetry();
        await Install(Package, ct);
        Hold(HeldType);

        registry.DownloadStatus = HttpStatusCode.NotFound;
        await Pass(lane, ct);
        registry.Downloads.Should().HaveCount(1);

        registry.DownloadStatus = HttpStatusCode.OK;
        await Pass(lane, ct);
        registry.Downloads.Should().HaveCount(2, "a 404 for the bytes is not an inspected bundle");
        registry.DownloadedVersions.Should().Equal(["1.2"]);
    }

    private Task<int> Pass(HeldNodeTypeBundleRetry lane, CancellationToken ct) =>
        Pass(lane, new PluginBundleClient(Mesh, RegistryUrl, Token), ct);

    private Task<int> Pass(HeldNodeTypeBundleRetry lane, PluginBundleClient client, CancellationToken ct) =>
        lane.Run(Mesh, client, RegistryUrl, "fleet", [Package], null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>
    /// The lane adopts the bundle of the index snapshot the PASS reads — not a fresher one. An
    /// earlier lane of the same pass (the content lane) has already read the client's shared index
    /// when the rebake lands; planning from a fresh read recorded the NEW bundle as tried while the
    /// adoption downloaded the OLD one, and the next pass then suppressed the new bundle.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheLane_PlansFromTheIndexItsAdoptionReads_SoARebakeBetweenLanesIsNotLost()
    {
        var ct = TestContext.Current.CancellationToken;
        var lane = new HeldNodeTypeBundleRetry();
        await Install(Package, ct);
        Hold(HeldType);

        // One pass's client: an earlier lane reads its shared index (version 1.2)…
        var client = new PluginBundleClient(Mesh, RegistryUrl, Token);
        await client.Adopt("AnEarlierLanesPackage").FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        // …and the rebake lands before this lane runs.
        registry.Version = "1.2.1";

        await Pass(lane, client, ct);
        registry.DownloadedVersions.Should().Equal(["1.2"], "the pass adopts the bundle of its own index snapshot");

        // The next pass reads the index afresh, sees 1.2.1 — never tried — and adopts it.
        await Pass(lane, ct);
        registry.DownloadedVersions.Should().Equal(["1.2", "1.2.1"],
            "the rebake was not recorded as tried by the pass that never downloaded it");
    }

    /// <summary>A transfer that fails is not an inspected bundle: the next pass asks again, with an
    /// unchanged index — unlike a bundle whose bytes arrived and were judged (the control below).</summary>
    [Fact(Timeout = 120_000)]
    public async Task ATransferThatFailed_IsNotRecordedAsTried_AndTheNextPassAsksAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var lane = new HeldNodeTypeBundleRetry();
        await Install(Package, ct);
        Hold(HeldType);

        registry.DownloadStatus = HttpStatusCode.ServiceUnavailable;
        await Pass(lane, ct);
        var failedAttempts = registry.Downloads.Count;
        failedAttempts.Should().BeGreaterThan(0);

        // The registry recovers; the index is unchanged.
        registry.DownloadStatus = HttpStatusCode.OK;
        await Pass(lane, ct);
        registry.Downloads.Should().HaveCount(failedAttempts + 1, "a failed transfer must not suppress the bundle");

        // Control: the bytes arrived and were judged — the same unchanged index is not downloaded again.
        await Pass(lane, ct);
        registry.Downloads.Should().HaveCount(failedAttempts + 1);
    }

    /// <summary>An adoption that never answers is bounded: the pass completes, and the stalled
    /// bundle is not recorded as tried.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AnAdoptionThatNeverAnswers_IsBoundedByTheBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var lane = new HeldNodeTypeBundleRetry { AdoptBudget = TimeSpan.FromSeconds(2) };
        await Install(Package, ct);
        Hold(HeldType);

        registry.StallDownloads = true;
        (await Pass(lane, ct)).Should().Be(0, "the pass ends at the budget rather than waiting for ever");

        registry.StallDownloads = false;
        var before = registry.Downloads.Count;
        await Pass(lane, ct);
        registry.Downloads.Should().HaveCount(before + 1, "a stalled transfer is not an inspected bundle");
    }

    [Fact(Timeout = 120_000)]
    public async Task EveryPass_AsksForAHeldTypesPackage_AndDownloadsOnlyABundleItHasNotTried()
    {
        var ct = TestContext.Current.CancellationToken;
        var lane = new HeldNodeTypeBundleRetry();
        await Install(Package, ct);
        Hold(HeldType);

        await Pass(lane, ct);
        registry.Downloads.Should().Equal([Package],
            "the first pass after the hold adopts the advertised bundle once — the old process had no reason to");

        await Pass(lane, ct);
        registry.Downloads.Should().HaveCount(1, "an unchanged index is not a reason to download again");
        registry.IndexReads.Should().BeGreaterThanOrEqualTo(2, "but every pass does ask");

        // The bake lands: the index advertises a new bundle for the same package.
        registry.Version = "1.2.1";
        await Pass(lane, ct);
        registry.Downloads.Should().Equal([Package, Package], "the new bake is adopted on the pass that sees it");
    }

    [Fact(Timeout = 120_000)]
    public async Task WithNothingHeld_TheIndexIsNotEvenRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var lane = new HeldNodeTypeBundleRetry();

        await Pass(lane, ct);
        registry.IndexReads.Should().Be(0);
        registry.Downloads.Should().BeEmpty();

        // Control: the same lane with a hold reads the index — the zero above is about the census.
        await Install(Package, ct);
        Hold(HeldType);
        await Pass(lane, ct);
        registry.IndexReads.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TheCensus_NamesExactlyTheRecordsInADeliveryHold()
    {
        var held = new NodeTypeDefinition { Configuration = "c", DeliveryHoldReason = "awaiting a bundle" };
        var free = new NodeTypeDefinition { Configuration = "c" };
        var census = NodeTypeLiveRecordCensus.Of(
            [("B/Held", held), ("A/Free", free), ("A/Held", held), ("C/Untyped", null)],
            PrebuiltAssemblySeeder.LiveFrameworkMvid, Now.AddHours(-1), Now);

        census.AwaitingBundle.Should().Equal("A/Held", "B/Held");
    }

    /// <summary>The plugin registry's bundle index and HTTP bundle route, logging what was asked.</summary>
    private sealed class FakeIndex : HttpMessageHandler, IHostHandler
    {
        private ImmutableList<string> downloads = ImmutableList<string>.Empty;
        private ImmutableList<string> downloadedVersions = ImmutableList<string>.Empty;
        private int indexReads;

        public string Version { get; set; } = "1.2";
        public HttpStatusCode DownloadStatus { get; set; } = HttpStatusCode.OK;
        public bool StallDownloads { get; set; }
        public ImmutableList<string> Downloads => downloads;
        public ImmutableList<string> DownloadedVersions => downloadedVersions;
        public int IndexReads => Volatile.Read(ref indexReads);

        public bool Serves(string host) => string.Equals(host, RegistryHost, StringComparison.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == $"{PluginBundleClient.RoutePrefix}/index.json")
            {
                Interlocked.Increment(ref indexReads);
                var body = JsonSerializer.Serialize(new
                {
                    frameworkMvid = PrebuiltAssemblySeeder.LiveFrameworkMvid,
                    bundles = new[]
                    {
                        new
                        {
                            plugin = Package,
                            version = Version,
                            url = $"{RegistryUrl}{PluginBundleClient.RoutePrefix}/{Package}/{Version}",
                        },
                    },
                }, PluginRegistryPayloads.Json);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
            if (path.StartsWith($"{PluginBundleClient.RoutePrefix}/", StringComparison.Ordinal))
            {
                var segments = path[(PluginBundleClient.RoutePrefix.Length + 1)..].Split('/');
                ImmutableInterlocked.Update(ref downloads, d => d.Add(Uri.UnescapeDataString(segments[0])));
                if (StallDownloads)
                    // A registry that accepted the request and never answers: completes only when
                    // the caller gives up (the lane's budget cancels the transfer).
                    return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        .ContinueWith(_ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout), TaskScheduler.Default);
                if (DownloadStatus != HttpStatusCode.OK)
                    return Task.FromResult(new HttpResponseMessage(DownloadStatus)
                    {
                        Content = new StringContent("registry unavailable"),
                    });
                if (segments.Length > 1)
                    ImmutableInterlocked.Update(ref downloadedVersions, d => d.Add(Uri.UnescapeDataString(segments[1])));
                // Not a bundle: the seeder adopts nothing from it, which is the point — the lane's
                // contract is WHEN it asks, and the adoption gates are the install's own.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("not a zip")),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
