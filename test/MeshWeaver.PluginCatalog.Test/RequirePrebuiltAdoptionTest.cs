using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

#pragma warning disable CS1591

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// 🚨 <b>The compile fallback is a POLICY, and a require-prebuilt mesh refuses it EARLY and by
/// NAME</b> (MeshWeaver#2193 §A — the 2026-08-25 incident: distribution misses surfaced as
/// "carried no assemblies — compiling instead" and the failure was only diagnosable four causal
/// steps later).
///
/// <para>With <c>Modules:RequirePrebuilt=true</c>, every adoption miss —
/// not advertised, not served for this lane, the registry down — FAILS the adoption with a
/// <see cref="PrebuiltRequiredException"/> whose message names the package, the registry, the
/// framework identity/architecture, the miss kind and the fix (publish/rebake the bundle for this
/// lane). Nothing compiles. The ledger still records every miss, so the flag never trades
/// observability for strictness.</para>
///
/// <para>The flag-OFF contract is pinned byte-for-byte by the existing
/// <see cref="BundleAdoptionMissTest"/> suite, which runs unchanged: same stub registry, same miss
/// shapes, bare zeros.</para>
/// </summary>
public class RequirePrebuiltAdoptionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RegistryUrl = "http://registry.test";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
            .ConfigureServices(services => services
                .AddSingleton<IHttpClientFactory>(new StubFactory(registry))
                // The deployment opt-in under test — the same registration idiom the Content tests
                // use for in-memory configuration.
                .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [PrebuiltAssemblySeeder.RequirePrebuiltConfigKey] = "true",
                    })
                    .Build()));

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

    private static async Task<PrebuiltRequiredException> ShouldRefuse(IObservable<int> adoption)
    {
        var act = () => adoption.FirstAsync().ToTask();
        return (await act.Should().ThrowAsync<PrebuiltRequiredException>()).Which;
    }

    /// <summary>A package the registry does not advertise fails EARLY, with every fact an operator
    /// needs in one message — and the miss is still ledgered.</summary>
    [Fact(Timeout = 120_000)]
    public async Task NotAdvertised_FailsEarly_NamingPackageRegistryIdentityAndFix()
    {
        registry.IndexJson = Index(PrebuiltAssemblySeeder.LiveFrameworkMvid, ("Other", "1.0.0"));

        var refusal = await ShouldRefuse(new PluginBundleClient(Mesh, RegistryUrl).Adopt("Store"));

        refusal.Message.Should().Contain(PrebuiltAssemblySeeder.RequirePrebuiltConfigKey,
            "the message must say WHICH policy refused");
        refusal.Message.Should().Contain("'Store'").And.Contain(RegistryUrl);
        refusal.Message.Should().Contain(PrebuiltAssemblySeeder.LiveFrameworkMvid,
            "…and WHICH lane the bundle is missing for");
        refusal.Message.Should().Contain("rebake",
            "…and WHAT fixes it — the fix is on the distribution side, never this mesh");

        Ledger.Misses.Should().ContainSingle()
            .Which.Kind.Should().Be(BundleAdoptionKind.NotAdvertised);
    }

    /// <summary>Advertised but not served for this lane — the incident's own shape — fails early
    /// instead of compiling.</summary>
    [Fact(Timeout = 120_000)]
    public async Task AdvertisedButNotServed_FailsEarly()
    {
        registry.IndexJson = Index(PrebuiltAssemblySeeder.LiveFrameworkMvid, ("Store", "1.0.0"));
        registry.DownloadStatus = HttpStatusCode.NotFound;

        var refusal = await ShouldRefuse(new PluginBundleClient(Mesh, RegistryUrl).Adopt("Store"));

        refusal.Message.Should().Contain("NotServed");
        Ledger.Misses.Should().ContainSingle()
            .Which.Kind.Should().Be(BundleAdoptionKind.NotServed);
    }

    /// <summary>A registry outage is ALSO a refusal here: "never start compiling" tolerates a
    /// failed install (retryable) but not a silent local build.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ARegistryOutage_FailsEarly_NotCompile()
    {
        registry.IndexJson = Index(PrebuiltAssemblySeeder.LiveFrameworkMvid, ("Store", "1.0.0"));
        registry.DownloadStatus = HttpStatusCode.ServiceUnavailable;

        var refusal = await ShouldRefuse(new PluginBundleClient(Mesh, RegistryUrl).Adopt("Store"));

        refusal.Message.Should().Contain("FetchFailed");
        Ledger.Misses.Should().ContainSingle()
            .Which.Kind.Should().Be(BundleAdoptionKind.FetchFailed);
    }

    // ── the default-install absorb policy (pure — no mesh) ─────────────────────────────────────

    /// <summary>Ordinary adoption failures stay absorbed (compiling is the default fallback), but
    /// the named refusal must PROPAGATE — swallowing it one call site above where it was refused
    /// would restore the silent compile the flag forbids.</summary>
    [Fact]
    public void AbsorbPolicy_SwallowsOrdinaryFailures_ButPropagatesTheNamedRefusal()
    {
        var ordinary = new Subject<int>();
        var absorbed = new List<int>();
        using (InstanceAutoRegistrationService
                   .AbsorbUnlessPrebuiltRequired(ordinary, logger: null, "Pack")
                   .Subscribe(absorbed.Add))
        {
            ordinary.OnError(new InvalidOperationException("registry hiccup"));
        }
        absorbed.Should().Equal([0], "an ordinary failure degrades to the compile fallback");

        var required = new Subject<int>();
        Exception? propagated = null;
        using (InstanceAutoRegistrationService
                   .AbsorbUnlessPrebuiltRequired(required, logger: null, "Pack")
                   .Subscribe(_ => { }, ex => propagated = ex))
        {
            required.OnError(new PrebuiltRequiredException("Modules:RequirePrebuilt: no bundle"));
        }
        propagated.Should().BeOfType<PrebuiltRequiredException>(
            "the named refusal must fail the install, visibly");
    }
}
