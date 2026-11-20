using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A replica whose module bundles come only from a DECLARED registry adopts them BEFORE the
/// boot sweep judges its types — so the sweep never counts a pending bundle as a regression, and
/// the publication gate never refuses the adoption because of the very types it covers.</b>
///
/// <para><b>The incident.</b> The dev instance's first platform-4 pod (2026-11-20). It installs its
/// packages from a registry only (<c>PluginCatalog:Registries</c>, no registry-fed root on disk) and
/// syncs none of their partitions. The warm-up started at 08:30:30Z; every module type was PARKED by
/// the untracked-module gate (<c>no prebuilt bundle for framework c004e001 names this type</c>); at
/// 08:42:10Z the sweep refused readiness on 53 regressions; ONLY THEN, from 08:42:39Z, the default
/// install's bundle client adopted — <c>adopted 0/25</c>, every stamp refused by the publication gate
/// because this process had failed its validation. The bundles existed on the registry the whole
/// time. Each step was right alone; together they deadlocked.</para>
///
/// <para><b>The reproduction is the real hosted warm-up</b> (<see cref="DynamicTypePreWarmerHostedService"/>)
/// on a real mesh: an ARMED bake gate, the real untracked-module gate (the GitSync tracking seam, the
/// partition carrying no <c>_GitSync</c>), a real install record, and the plugin catalog's real
/// registration. The only stand-in is the registry's HTTP endpoint, at the
/// <see cref="IHttpClientFactory"/> seam production plugs into: an index plus one bundle zip written
/// by <see cref="BundleWriter"/> for the live framework identity.</para>
///
/// <para><b>The control.</b> <see cref="NegativeControl_ARegistryThatServesNoBundleForTheType_StillRefusesReadiness"/>
/// runs the identical fixture with a registry index that names no bundle for the package: the gate
/// must still regress on the type. That proves the fixture produces the regression the fix removes,
/// and that the fix relaxed nothing — a type no declared source covers is still refused.</para>
/// </summary>
public class ARegistryOnlyInstallationAdoptsBeforeTheSweepJudgesTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RegistryUrl = "https://registry.test";
    private const string Version = "1.0.0";

    /// <summary>The coordinates the record names on the way in — a working build of the PREVIOUS platform.</summary>
    private const string OldMvid = "0000aaaa0000aaaa";
    private const string OldFramework = "c003e001";

    private readonly string package = "Regpkg" + Guid.NewGuid().ToString("N")[..8];
    private readonly FakeRegistry registry = new();

    private string TypePath => $"{package}/Widget";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .AddPluginCatalog()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // The portal's wiring: the gate is ARMED (a readiness probe consumes it).
                services.AddNodeTypeBakeGate(gatesReadiness: true);
                services.AddSingleton(new PluginCatalogOptions
                {
                    InstallPreInstalledPackages = false,
                    // A plain token: the resolver hands a non-instance-key token back as configured.
                    Registries = [new PluginRegistryReference { Name = "Plugins", Url = RegistryUrl, Token = "test-token" }],
                });
                services.AddSingleton<IHttpClientFactory>(new FakeFactory(registry));
                // Layer the sweep switch ONTO the host configuration.
                var configured = services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration));
                if (configured is not null)
                    services.Remove(configured);
                return services.AddSingleton<IConfiguration>(sp =>
                {
                    var configuration = new ConfigurationBuilder();
                    if (Materialise(configured, sp) is { } host)
                        configuration.AddConfiguration(host);
                    return configuration.AddInMemoryCollection(
                        System.Collections.Immutable.ImmutableDictionary<string, string?>.Empty
                            .Add(DynamicTypePreWarmerHostedService.EnabledConfigKey, "true")).Build();
                });
            });

    private static IConfiguration? Materialise(ServiceDescriptor? descriptor, IServiceProvider sp)
        => descriptor is null ? null
            : descriptor.ImplementationFactory is { } factory ? (IConfiguration)factory(sp)
            : descriptor.ImplementationInstance is IConfiguration instance ? instance
            : throw new InvalidOperationException(
                "The IConfiguration registration is neither a factory nor an instance, so this test "
                + "cannot layer onto it.");

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();
    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private NodeTypeBakeGateState Gate => Mesh.ServiceProvider.GetRequiredService<NodeTypeBakeGateState>();

    private static byte[] AssemblyBytes() =>
        File.ReadAllBytes(typeof(ARegistryOnlyInstallationAdoptsBeforeTheSweepJudgesTest).Assembly.Location);

    /// <summary>The installation as the previous platform left it: an install record, and the
    /// package's module type in its own partition, carrying a working build of the old identity.</summary>
    private async Task SeedInstallation(CancellationToken ct)
    {
        using (Access.ImpersonateAsSystem())
        {
            await MeshService.CreateNode(new MeshNode(package, PackageInstaller.InstalledPartition)
                {
                    Name = package,
                    NodeType = PackageInstaller.PackageNodeType,
                    State = MeshNodeState.Active,
                    Content = new PackageManifest
                    {
                        Id = package, Name = package, Source = "Plugins", Version = Version, TargetPartition = package,
                    },
                })
                .Should().Within(TestTimeouts.Convergence).Emit("the install record is written", ct);
            await MeshService.CreateNode(MeshNode.FromPath(TypePath) with
                {
                    Name = "Widget",
                    NodeType = MeshNode.NodeTypePath,
                    State = MeshNodeState.Active,
                    Content = new NodeTypeDefinition
                    {
                        Configuration = "config => config",
                        CompilationStatus = CompilationStatus.Ok,
                        LatestAssemblyCollection = "assemblies",
                        LatestAssemblyPath = "old/Widget.dll",
                        LatestAssemblyMvid = OldMvid,
                        LastCompiledVersion = 1,
                        CompiledFrameworkVersion = OldFramework,
                        // Module content by the record's own witness: it adopted a bundle before.
                        AdoptedSourceFingerprint = "bundlefingerprint0000000",
                        BuildProvenance = BuildProvenance.AdoptedVerified,
                    },
                })
                .Should().Within(TestTimeouts.Convergence).Emit("the module type is written", ct);
        }
        var tracking = Mesh.ServiceProvider.GetServices<IPartitionSourceTracking>().ToArray();
        tracking.Should().NotBeEmpty("precondition — the untracked-module gate only fires where a tracking seam exists");
        var tracked = await tracking
            .Select(t => t.IsTracked(package).Take(1))
            .CombineLatest()
            .Select(answers => answers.Any(a => a))
            .Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the tracking seam answers", ct);
        tracked.Should().BeFalse("precondition — the package's partition tracks no source, so only a bundle may serve it");
    }

    private byte[] Bundle()
    {
        using var stream = new MemoryStream();
        BundleWriter.Write(
            stream, plugin: package, version: Version,
            frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid,
            assemblies: [new BundleWriter.AssemblyEntry(TypePath, () => new MemoryStream(AssemblyBytes()))]);
        return stream.ToArray();
    }

    /// <summary>Runs the hosted warm-up exactly as a portal does and waits for its terminal verdict.</summary>
    private async Task<NodeTypeBakeGateState> WarmUp(CancellationToken ct)
    {
        using var lifetime = new StartedLifetime();
        using var service = new DynamicTypePreWarmerHostedService(
            Mesh.ServiceProvider, lifetime,
            Mesh.ServiceProvider.GetRequiredService<ILogger<DynamicTypePreWarmerHostedService>>());
        await service.StartAsync(ct);
        lifetime.Start();
        return await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .Select(_ => Gate)
            .Where(g => g.Phase is BakePhase.Complete or BakePhase.Regressed or BakePhase.Faulted)
            .Take(1)
            .Should().Within(TestTimeouts.CrossSilo * 3)
            .Emit("the warm-up reaches a terminal verdict", ct);
    }

    [Fact(Timeout = 300_000)]
    public async Task ADeclaredRegistryBundle_IsAdoptedBeforeTheSweepJudges_AndTheReplicaIsAdmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        registry.Package = package;
        registry.Bundle = Bundle();
        await SeedInstallation(ct);
        var bundleMvid = ServedBuildIdentity.OfBytes(AssemblyBytes())!;

        var gate = await WarmUp(ct);

        gate.Regressions.Keys.Should().NotContain(TypePath,
            $"a type whose bundle the declared registry serves must not be judged before it is offered; "
            + $"regressions: {string.Join(" | ", gate.Regressions.Select(r => $"{r.Key} → {r.Value}"))}");
        registry.Downloads.Should().BeGreaterThan(0,
            "the declared registry was ASKED for the package — without it nothing above tests anything");
        gate.Phase.Should().Be(BakePhase.Complete);
        gate.ReadinessGranted.Should().BeTrue(
            "the replica measured every type it serves as usable — the incident's pod refused readiness for ever");

        await Mesh.GetMeshNodeStream(TypePath)
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions))
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(d => string.Equals(d?.LatestAssemblyMvid, bundleMvid, StringComparison.OrdinalIgnoreCase)
                        && d?.CompiledFrameworkVersion == PrebuiltAssemblySeeder.LiveFrameworkMvid,
                "the adoption stamp that was HELD while the bake measured is released on admission — the "
                + "shared record now names the registry's bytes for this framework", ct);
    }

    [Fact(Timeout = 300_000)]
    public async Task NegativeControl_ARegistryThatServesNoBundleForTheType_StillRefusesReadiness()
    {
        var ct = TestContext.Current.CancellationToken;
        // The registry answers, but names no bundle for the package.
        registry.Package = null;
        await SeedInstallation(ct);

        var gate = await WarmUp(ct);

        gate.Phase.Should().Be(BakePhase.Regressed,
            "with no declared source covering the type the gate is exactly as strict as before — the fix "
            + "orders the adoption, it relaxes no verdict");
        gate.Regressions.Keys.Should().Contain(TypePath);
        gate.ReadinessGranted.Should().BeFalse();
        registry.Downloads.Should().Be(0, "nothing was advertised, so nothing was downloaded");
    }

    /// <summary>The registry's bundle routes: its index, and one package's bundle, counted.</summary>
    private sealed class FakeRegistry : HttpMessageHandler
    {
        private int downloads;

        /// <summary>The package the index advertises a bundle for, or null for an index naming none.</summary>
        public string? Package { get; set; }

        public byte[] Bundle { get; set; } = [];

        public int Downloads => Volatile.Read(ref downloads);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/index.json", StringComparison.Ordinal))
            {
                var bundles = Package is null
                    ? ""
                    : "{\"plugin\":\"" + Package + "\",\"version\":\"" + Version + "\",\"url\":\"" + RegistryUrl
                      + PluginBundleClient.RoutePrefix + "/" + Package + "/" + Version + "\"}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"frameworkMvid\":\"" + PrebuiltAssemblySeeder.LiveFrameworkMvid + "\",\"bundles\":[" + bundles + "]}",
                        Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")),
                });
            }
            if (Package is not null
                && path.StartsWith(PluginBundleClient.RoutePrefix + "/" + Package + "/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref downloads);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Bundle),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StartedLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public void StopApplication() { }
        public void Start() => started.Cancel();

        public void Dispose()
        {
            started.Dispose();
            stopping.Dispose();
            stopped.Dispose();
        }
    }
}
