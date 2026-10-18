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

    private Task<int> Pass(HeldNodeTypeBundleRetry lane, CancellationToken ct) =>
        lane.Run(Mesh, new PluginBundleClient(Mesh, RegistryUrl, Token), RegistryUrl, "fleet", [Package], null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    [Fact(Timeout = 120_000)]
    public async Task EveryPass_AsksForAHeldTypesPackage_AndDownloadsOnlyABundleItHasNotTried()
    {
        var ct = TestContext.Current.CancellationToken;
        var lane = new HeldNodeTypeBundleRetry();
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
        private int indexReads;

        public string Version { get; set; } = "1.2";
        public ImmutableList<string> Downloads => downloads;
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
                var package = path[(PluginBundleClient.RoutePrefix.Length + 1)..].Split('/')[0];
                ImmutableInterlocked.Update(ref downloads, d => d.Add(Uri.UnescapeDataString(package)));
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
