#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// 🚨 <b>A fetch MISS must stay loud and countable</b> (#1782 gap 4).
///
/// <para>Adoption's evidence was a log line, and the miss that matters most had none at all: when
/// the registry's index does not advertise a package for this lane, <c>Adopt</c> returned the bare
/// integer 0 — the SAME value a fully successful adoption returns — with no log and no counter.
/// The compile that followed looked exactly like normal behaviour. That is the consumer's view of
/// the 2026-08-20 outage: an empty index, every consumer quietly compiling, nothing anywhere
/// saying so. With instance-level pre-bake giving way to lazy compile-on-access (#1746) the fetch
/// path becomes the PRIMARY way assemblies arrive, so the metric that proves it works is the only
/// reason anyone would notice it stopping.</para>
///
/// <para>The registry is stubbed at the <see cref="HttpMessageHandler"/> — no sockets, no ports —
/// so the client's real index read, real decline gate and real miss accounting all run.</para>
/// </summary>
public class BundleAdoptionMissTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RegistryUrl = "http://registry.test";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Answers the bundle routes with whatever the test staged.</summary>
    private sealed class StubRegistry : HttpMessageHandler
    {
        public string? IndexJson { get; set; }
        public HttpStatusCode DownloadStatus { get; set; } = HttpStatusCode.NotFound;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/index.json", StringComparison.Ordinal))
                return Task.FromResult(IndexJson is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(IndexJson, Encoding.UTF8, "application/json"),
                    });

            return Task.FromResult(new HttpResponseMessage(DownloadStatus)
            {
                Content = new StringContent(string.Empty),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly StubRegistry registry = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddGraph().AddPluginCatalog()
            .ConfigureServices(services =>
                services.AddSingleton<IHttpClientFactory>(new StubFactory(registry)));

    private BundleAdoptionLedger Ledger =>
        Mesh.ServiceProvider.GetRequiredService<BundleAdoptionLedger>();

    private static string Index(string identity, params (string Plugin, string Version)[] bundles) =>
        JsonSerializer.Serialize(new
        {
            frameworkMvid = identity,
            architecture = ReleaseArchitecture.Live,
            bundles = bundles.Select(b => new
            {
                plugin = b.Plugin,
                version = b.Version,
                url = $"{RegistryUrl}/api/plugins/bundles/{b.Plugin}/{b.Version}",
            }).ToArray(),
        }, Json);

    /// <summary>
    /// 🚨 THE defect. The registry answers with an index this instance CAN adopt from — same
    /// framework identity, same architecture — that simply does not list the package. Before, that
    /// was an unannotated `return 0`.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APackageTheRegistryDoesNotAdvertise_IsRecordedAsAMiss_NotAsASilentZero()
    {
        registry.IndexJson = Index(PrebuiltAssemblySeeder.LiveFrameworkMvid, ("Other", "1.0.0"));

        var adopted = await new PluginBundleClient(Mesh, RegistryUrl)
            .Adopt("Store").FirstAsync().Await();

        // The RETURN is deliberately unchanged — adoption must never fail an install.
        adopted.Should().Be(0);

        var miss = Ledger.Misses.Should().ContainSingle().Subject;
        miss.PluginId.Should().Be("Store");
        miss.Kind.Should().Be(BundleAdoptionKind.NotAdvertised);
        miss.Registry.Should().Be(RegistryUrl);
        miss.Reason.Should().Contain("1 package(s)",
            "naming how many ARE advertised separates 'the index is empty' from 'my grant excludes me'");
        Ledger.Describe().Should().Contain("MISS");
        Ledger.Describe().Should().Contain("Store");
    }

    /// <summary>
    /// The whole index baked for another framework build is a DIFFERENT miss from "not advertised",
    /// and it must not be flattened into one. One is normal during a platform roll; the other means
    /// the package is not being distributed at all.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnIndexForAnotherFrameworkBuild_IsItsOwnKindOfMiss()
    {
        registry.IndexJson = Index(Guid.NewGuid().ToString("N"), ("Store", "1.0.0"));

        (await new PluginBundleClient(Mesh, RegistryUrl).Adopt("Store").FirstAsync().Await())
            .Should().Be(0);

        Ledger.Misses.Should().ContainSingle()
            .Which.Kind.Should().Be(BundleAdoptionKind.FrameworkDeclined);
    }

    /// <summary>
    /// Advertised but not served for this lane — the third distinct miss. A bare <c>byte[]?</c>
    /// collapsed this and a registry outage into the same null, and the caller collapsed that into
    /// the same 0 as a success.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AdvertisedButNotServedForThisLane_IsRecordedSeparatelyFromAnOutage()
    {
        registry.IndexJson = Index(PrebuiltAssemblySeeder.LiveFrameworkMvid, ("Store", "1.0.0"));
        registry.DownloadStatus = HttpStatusCode.NotFound;

        (await new PluginBundleClient(Mesh, RegistryUrl).Adopt("Store").FirstAsync().Await())
            .Should().Be(0);
        Ledger.Misses.Should().ContainSingle()
            .Which.Kind.Should().Be(BundleAdoptionKind.NotServed);
    }

    [Fact(Timeout = 120_000)]
    public async Task ARegistryThatIsDown_IsAFetchFailure_NotAMissingPackage()
    {
        registry.IndexJson = Index(PrebuiltAssemblySeeder.LiveFrameworkMvid, ("Store", "1.0.0"));
        registry.DownloadStatus = HttpStatusCode.ServiceUnavailable;

        (await new PluginBundleClient(Mesh, RegistryUrl).Adopt("Store").FirstAsync().Await())
            .Should().Be(0);
        Ledger.Misses.Should().ContainSingle()
            .Which.Kind.Should().Be(BundleAdoptionKind.FetchFailed);
    }

    // ── the ledger itself: pure, and the sentence it must not say ───────────────────────────────

    /// <summary>
    /// 🚨 "Nothing was ever attempted" and "everything was adopted" are DIFFERENT sentences. A
    /// deployment with no registry never attempts adoption, and reporting that as a clean sweep
    /// would make the absence of the lane look like the success of it.
    /// </summary>
    [Fact]
    public void AnEmptyLedgerSaysNothingWasAttempted_NeverThatEverythingWasAdopted()
    {
        var ledger = new BundleAdoptionLedger();

        ledger.Describe().Should().Be("no bundle adoption has been attempted in this process");
        ledger.Describe().Should().NotContain("no misses");
        ledger.Misses.Should().BeEmpty();
    }

    /// <summary>
    /// A PARTIAL adoption is a partial miss. Rounding "adopted 3 of 12" up to "adopted" is how a
    /// regression hides inside a success — the other nine are compiled here, which is exactly the
    /// cost this lane exists to remove.
    /// </summary>
    [Fact]
    public void APartialAdoptionCountsAsAMiss()
    {
        var ledger = new BundleAdoptionLedger();
        ledger.Record(new BundleAdoptionOutcome(
            "Store", BundleAdoptionKind.Adopted, RegistryUrl, Adopted: 3, Offered: 12));
        ledger.Record(new BundleAdoptionOutcome(
            "Edu", BundleAdoptionKind.Adopted, RegistryUrl, Adopted: 5, Offered: 5));

        ledger.Misses.Should().ContainSingle().Which.PluginId.Should().Be("Store");
        ledger.Describe().Should().Contain("adopted only 3/12");
    }

    /// <summary>
    /// Bounded: a diagnostic on a long-lived process answers "is the lane working NOW" from the
    /// last N attempts. An unbounded list would answer it and also leak.
    /// </summary>
    [Fact]
    public void TheLedgerIsBounded()
    {
        var ledger = new BundleAdoptionLedger();
        for (var i = 0; i < BundleAdoptionLedger.Capacity + 50; i++)
            ledger.Record(new BundleAdoptionOutcome(
                $"P{i}", BundleAdoptionKind.NotAdvertised, RegistryUrl));

        ledger.Outcomes.Count.Should().Be(BundleAdoptionLedger.Capacity);
        ledger.Outcomes[^1].PluginId.Should().Be($"P{BundleAdoptionLedger.Capacity + 49}",
            "the newest attempts are the ones that answer 'is it working now'");
    }
}
