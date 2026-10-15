#pragma warning disable CS1591

using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#6831 — a registry's own-lane serve labels each assembly with the module version
/// THOSE BYTES were built at, never the version the package is advertised at.
///
/// <para>Measured on two portals: the registry advertised <c>Personal</c> at 1.1.1 while its
/// <c>Personal/PersonalApp</c> record served the 1.0.1 build (1.1.1's only bake had been cancelled),
/// and the consumer's record then read <c>buildProvenance: AdoptedUnverified,
/// adoptedModuleVersion: 1.1.1</c> over the 1.0.1 MVID. <c>BuildResult</c> wrote the package
/// version as the manifest's only version, and <c>PluginBundleClient</c> stamped every assembly
/// with it.</para>
///
/// <para>Real authenticated route, real install record, real assembly store. Two types in one
/// package at 1.1.1: one whose current build is an ADOPTION at 1.0.1 (the issue's record), and one
/// compiled here from the current source (the control: it IS 1.1.1). The consumer half is the real
/// <see cref="BundleReader"/>, which is what <c>PluginBundleClient</c> seeds from.</para>
/// </summary>
public class RegistryStatesTheServedBuildsModuleVersionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Plugin = "LabelledPersonal";
    private const string Source = "Plugins";
    private const string Instance = "served-version-consumer";
    private const string AdvertisedVersion = "1.1.1";
    private const string BuiltVersion = "1.0.1";
    private const string AdoptedType = Plugin + "/PersonalApp";
    private const string CompiledType = Plugin + "/Fresh";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    [Fact(Timeout = 300_000)]
    public async Task AnOlderBuildServedUnderANewerPackageVersion_IsLabelledWithItsOwnVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await InstallRecord(ct);
        await CreateTypeWithBuild(AdoptedType, storeVersion: 1, new NodeTypeDefinition
        {
            BuildProvenance = BuildProvenance.AdoptedUnverified,
            AdoptedModuleVersion = BuiltVersion,
            CurrentModuleVersion = AdvertisedVersion,
        }, ct);
        await CreateTypeWithBuild(CompiledType, storeVersion: 1, new NodeTypeDefinition
        {
            BuildProvenance = BuildProvenance.Compiled,
            CurrentModuleVersion = AdvertisedVersion,
            // The compile recorded the version of these bytes (the helper keys it to their MVID).
            BuiltModuleVersion = AdvertisedVersion,
        }, ct);

        var key = await RegisterInstance(ct);
        await using var app = await StartBundleHost(ct);
        var bundle = await Bundle(app, AdvertisedVersion, key, ct);

        var (manifest, payloads) = BundleReader.Read(bundle);
        Assert.NotNull(manifest);
        Assert.Equal(AdvertisedVersion, manifest!.Version);

        // The issue's record: the 1.0.1 bytes, served under the package's 1.1.1, must say 1.0.1 —
        // what the consumer stamps as adoptedModuleVersion.
        var adopted = Assert.Single(payloads, p => p.NodePath == AdoptedType);
        Assert.Equal(BuiltVersion, adopted.ModuleVersion);
        Assert.True(manifest.ModuleVersionPerAssembly == true,
            "the registry states each assembly's own version, so an unstated one is unknown, never the package's");

        var compiled = Assert.Single(payloads, p => p.NodePath == CompiledType);
        Assert.Equal(AdvertisedVersion, compiled.ModuleVersion);
    }

    /// <summary>The pure rule the registry reads the version through, for a record written BEFORE
    /// the version was keyed to the bytes' MVID: only a judged adoption can still say; every other
    /// historical record is unknown, never a guess from the live source. The transitions that write
    /// the keyed version are pinned in <c>BuiltModuleVersionFollowsTheBytesTest</c>.</summary>
    [Theory]
    [InlineData(BuildProvenance.AdoptedVerified, false, "1.0.1")]
    [InlineData(BuildProvenance.AdoptedUnverified, false, "1.0.1")]
    [InlineData(BuildProvenance.StaleAdopted, true, null)]
    [InlineData(BuildProvenance.AdoptionRefused, true, null)]
    [InlineData(BuildProvenance.Compiled, false, null)]
    [InlineData(BuildProvenance.Compiled, true, null)]
    public void TheServedBuildsVersion_ComesFromTheBuild_NeverFromTheSource(
        BuildProvenance provenance, bool dirty, string? expected)
    {
        var live = System.Collections.Immutable.ImmutableDictionary<string, long>.Empty.Add("T/Source/A", 2);
        var def = new NodeTypeDefinition
        {
            BuildProvenance = provenance,
            AdoptedModuleVersion = BuiltVersion,
            CurrentModuleVersion = AdvertisedVersion,
            CurrentSourceVersions = live,
            CompiledSources = dirty ? live.SetItem("T/Source/A", 1) : live,
        };
        Assert.Equal(expected, PrebuiltAssemblySeeder.ModuleVersionOfBuild(def));
    }

    /// <summary>A bake's sealed bundle states no per-assembly version and no flag: every assembly
    /// was built at the bundle's version, exactly as before.</summary>
    [Fact]
    public void ABundleWithoutPerAssemblyVersions_KeepsTheBundleVersion()
    {
        var manifest = new BundleReader.Manifest(Plugin, AdvertisedVersion, "fw", []);
        Assert.Equal(AdvertisedVersion, manifest.ModuleVersionOf(new BundleReader.AssemblyRef(AdoptedType, "a.dll")));
        var perAssembly = manifest with { ModuleVersionPerAssembly = true };
        Assert.Null(perAssembly.ModuleVersionOf(new BundleReader.AssemblyRef(AdoptedType, "a.dll")));
    }

    private async Task CreateTypeWithBuild(
        string path, long storeVersion, NodeTypeDefinition shape, CancellationToken ct)
    {
        var store = Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>();
        var bytes = await File.ReadAllBytesAsync(typeof(BundleReader).Assembly.Location, ct);
        var location = await store.PutWithLocation(path, storeVersion, bytes, null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        var node = MeshNode.FromPath(path) with
        {
            Name = path[(path.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = shape with
            {
                Configuration = "config => config",
                CompilationStatus = CompilationStatus.Ok,
                LastCompiledVersion = storeVersion,
                LatestAssemblyCollection = location.Collection,
                LatestAssemblyPath = location.ContentPath,
                LatestAssemblyMvid = ServedBuildIdentity.OfBytes(bytes),
                BuiltModuleVersionMvid = shape.BuiltModuleVersion is null ? null : ServedBuildIdentity.OfBytes(bytes),
                CompiledFrameworkVersion = PrebuiltAssemblySeeder.LiveFrameworkMvid,
            },
        };
        using (Mesh.ServiceProvider.GetRequiredService<AccessService>().ImpersonateAsSystem())
            await Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(node)
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
    }

    private Task<InstallResult> InstallRecord(CancellationToken ct) =>
        PackageInstaller.Install(
                Mesh,
                new PackageManifest
                {
                    Id = Plugin,
                    Name = Plugin,
                    Kind = PackageKind.Content,
                    TargetPartition = Plugin,
                    SourceFolder = Plugin,
                    Version = "content-path-commit",
                    ReleasedVersion = AdvertisedVersion,
                    Source = Source,
                },
                [new PackageFile($"{Plugin}/Doc.md", "# Served version")],
                "HEAD")
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    private Task<string> RegisterInstance(CancellationToken ct) =>
        new MeshWeaverInstanceService(
                Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
                Mesh,
                Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:0"] = $"{Source}/*",
                    })
                    .Build())
            .Register("served-owner", "Served Owner", "owner@test.com", Instance, Instance)
            .Select(result => result.RawKey)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    private async Task<WebApplication> StartBundleHost(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(Mesh.ServiceProvider.GetRequiredService<InstanceRegistryAuthenticator>());
        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync(ct);
        return app;
    }

    private static async Task<byte[]> Bundle(WebApplication app, string version, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PluginBundleEndpoints.RoutePrefix}/{Plugin}/{version}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        using var response = await app.GetTestClient().SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
