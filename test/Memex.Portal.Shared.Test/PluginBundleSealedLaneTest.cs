#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A registry serves the lane the CALLER asked for, when it holds a publication sealed for
/// it — and its OWN lane when it does not</b> (MeshWeaver#3768).
///
/// <para>The defect: the index stamped <c>frameworkMvid</c> with the registry's own bake
/// unconditionally, and <c>PluginBundleClient.Adopt</c> compares that ONCE and declines the whole
/// index before requesting any package. So the download route's lane-awareness (#1751) and the
/// module half's sealed read (#3244) were both unreachable for exactly the consumer they exist for.
/// Measured on the fleet's own portals twice, with a different identity pair each time — 25
/// adoption attempts, 0 adopted, 25 <c>FrameworkDeclined</c> — while the publication sealed for the
/// consumer's identity sat on the share the registry mounts.</para>
///
/// <para><b>Both directions are asserted here, because a change that serves everybody passes a
/// one-sided test.</b> A caller whose lane IS sealed here must be told so and handed THOSE bytes; a
/// caller whose lane is NOT must still be told this registry's own identity — which is what keeps
/// it declining and compiling — and must never be handed another identity's bundle in its place.
/// The adoption gate itself (<see cref="PrebuiltAssemblySeeder.DeclineReason"/>, ordinal equality)
/// is untouched by the change and asserted here as the thing that still holds.</para>
/// </summary>
public class PluginBundleSealedLaneTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The lane the CONSUMER runs — not this test host's, which is what makes it "off
    /// lane". A fixed literal: the value must not be whatever the process happens to resolve.</summary>
    private const string ConsumerIdentity = "s11111111111111111111111111111111";

    /// <summary>A third lane nothing is sealed for — the negative control's ask.</summary>
    private const string UnsealedIdentity = "s22222222222222222222222222222222";

    private const string Package = "SealedLanePackage";
    private const string NodeTypePath = Package + "/Widget";
    private const string Source = "plugins";
    private const string Version = "3.2.1";
    private const string Instance = "sealed-lane-consumer";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    /// <summary>
    /// POSITIVE CONTROL — the caller's lane IS sealed here, so the index claims it and the bundle
    /// carries the sealed publication's own bytes, stamped with the caller's identity.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACallerWhoseLaneIsSealedHere_IsToldThatLane_AndServedThePublicationsBytes()
    {
        var root = NewRoot();
        try
        {
            SealPublication(root, ConsumerIdentity);
            await InstallPackage();
            var key = await RegisterInstance($"{Source}/*");
            await using var app = await StartHost(root);

            var index = await ReadIndex(app, key, ConsumerIdentity);

            Assert.Equal(ConsumerIdentity, index.GetProperty("frameworkMvid").GetString());
            Assert.Equal(
                ReleaseArchitecture.Live, index.GetProperty("architecture").GetString());

            var url = BundleUrl(index);
            Assert.NotNull(url);

            using var response = await Get(app, url! + LaneQuery(ConsumerIdentity), key);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var (manifest, assemblies) = BundleReader.Read(
                await response.Content.ReadAsByteArrayAsync());

            Assert.Equal(ConsumerIdentity, manifest?.FrameworkMvid);
            Assert.Equal(NodeTypePath, Assert.Single(assemblies).NodePath);
            // The producer's source fingerprint rides through, so the consuming hub can still
            // refuse an adoption whose source disagrees with the source that mesh holds (#2813).
            Assert.Equal("fp-sealed", Assert.Single(assemblies).SourceFingerprint);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// NEGATIVE CONTROL — nothing is sealed for the caller's lane, so the index keeps stating THIS
    /// registry's own identity. That is what makes the consumer decline and compile, and it is the
    /// assertion a fix that simply echoed the request back would fail.
    ///
    /// <para>The second half matters as much: a publication sealed for a DIFFERENT identity is
    /// present on the same root, and it must not be handed over in place of the lane that was
    /// asked for. Serving bytes baked for another framework is the outage this gate exists to
    /// prevent.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACallerWhoseLaneIsNotSealedHere_IsToldThisRegistrysOwnLane_AndGetsNoOtherLanesBytes()
    {
        var root = NewRoot();
        try
        {
            SealPublication(root, ConsumerIdentity);          // a lane that is NOT the one asked for
            await InstallPackage();
            var key = await RegisterInstance($"{Source}/*");
            await using var app = await StartHost(root);

            var index = await ReadIndex(app, key, UnsealedIdentity);

            Assert.Equal(
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                index.GetProperty("frameworkMvid").GetString());
            Assert.NotEqual(UnsealedIdentity, index.GetProperty("frameworkMvid").GetString());

            // …and the consumer's unchanged gate turns exactly that into a decline naming both.
            var decline = PrebuiltAssemblySeeder.DeclineReason(
                index.GetProperty("frameworkMvid").GetString(), UnsealedIdentity);
            Assert.NotNull(decline);
            Assert.Contains(UnsealedIdentity, decline);

            var url = BundleUrl(index);
            Assert.NotNull(url);

            using var response = await Get(app, url! + LaneQuery(UnsealedIdentity), key);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var (manifest, assemblies) = BundleReader.Read(
                    await response.Content.ReadAsByteArrayAsync());
                Assert.NotEqual(ConsumerIdentity, manifest?.FrameworkMvid);
                Assert.Empty(assemblies);
            }
            else
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A caller that states NO lane is answered exactly as before — the shape every already
    /// deployed consumer asks in, which must not move when a newer one starts stating its lane.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACallerThatStatesNoLane_IsAnsweredWithThisRegistrysOwnIdentity()
    {
        var root = NewRoot();
        try
        {
            SealPublication(root, ConsumerIdentity);
            await InstallPackage();
            var key = await RegisterInstance($"{Source}/*");
            await using var app = await StartHost(root);

            var index = await ReadIndex(app, key, identity: null);

            Assert.Equal(
                PrebuiltAssemblySeeder.LiveFrameworkMvid,
                index.GetProperty("frameworkMvid").GetString());
            Assert.Equal(
                ReleaseArchitecture.Live, index.GetProperty("architecture").GetString());
        }
        finally
        {
            Delete(root);
        }
    }

    // ───────────────────────────── harness ─────────────────────────────

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "mw3768-" + Guid.NewGuid().ToString("N"));

    private static void Delete(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A temp tree that will not delete must never fail the assertion that already passed.
        }
    }

    /// <summary>Writes a complete publication — one real bundle plus the seal that lists it, in the
    /// layout <c>&lt;root&gt;/&lt;identity&gt;/&lt;source&gt;/</c> the bake produces.</summary>
    private static void SealPublication(string root, string identity)
    {
        var directory = Path.Combine(root, identity, Source);
        Directory.CreateDirectory(directory);
        var bundle = Path.Combine(directory, $"{Package}.zip");
        using (var file = File.Create(bundle))
            BundleWriter.Write(
                file, Package, Version, identity,
                [
                    new BundleWriter.AssemblyEntry(
                        NodeTypePath,
                        () => new MemoryStream(AssemblyBytes()),
                        Dependencies: new Dictionary<string, string>(StringComparer.Ordinal))
                    {
                        SourceFingerprint = "fp-sealed",
                    },
                ]);
        File.WriteAllText(
            Path.Combine(directory, ShippedPrebuiltBundles.CompletionSentinelFileName),
            $"{Package}.zip\n");
    }

    /// <summary>Real PE bytes — a bundle carrying something that is not an assembly would be a
    /// weaker subject than the one this route actually serves.</summary>
    private static byte[] AssemblyBytes() =>
        File.ReadAllBytes(typeof(BundleWriter).Assembly.Location);

    private Task<InstallResult> InstallPackage() =>
        PackageInstaller.Install(
                Mesh,
                new PackageManifest
                {
                    Id = Package,
                    Name = Package,
                    Kind = PackageKind.Content,
                    TargetPartition = Package,
                    SourceFolder = Package,
                    Version = "1.0.0",
                    ReleasedVersion = Version,
                    Source = Source,
                },
                [new PackageFile($"{Package}/Doc.md", $"# {Package}")],
                "HEAD")
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(120))
            .Await();

    private Task<string> RegisterInstance(params string[] defaultGrants) =>
        new MeshWeaverInstanceService(
                Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>(),
                Mesh,
                Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(defaultGrants.Select((entry, i) =>
                        new KeyValuePair<string, string?>(
                            $"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:{i}", entry)))
                    .Build())
            .Register("sealed-lane-owner", "Owner", "owner@test.com", Instance, Instance)
            .Select(r => r.RawKey)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .Await();

    private async Task<WebApplication> StartHost(string publishedRoot)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PublishedBundleCatalogue.PublishedRootConfigKey] = publishedRoot,
        });
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(new InstanceRegistryAuthenticator(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>()));
        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync();
        return app;
    }

    private static string LaneQuery(string identity) =>
        $"?identity={Uri.EscapeDataString(identity)}"
        + $"&arch={Uri.EscapeDataString(ReleaseArchitecture.Live)}";

    private static async Task<JsonElement> ReadIndex(
        WebApplication app, string key, string? identity)
    {
        var route = PluginBundleEndpoints.RoutePrefix + "/index.json"
                    + (identity is null ? "" : LaneQuery(identity));
        using var response = await Get(app, route, key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>The relative bundle route the index advertises for the package under test.</summary>
    private static string? BundleUrl(JsonElement index) =>
        index.GetProperty("bundles").EnumerateArray()
            .Where(b => string.Equals(
                b.GetProperty("plugin").GetString(), Package, StringComparison.OrdinalIgnoreCase))
            .Select(b => new Uri(b.GetProperty("url").GetString()!).PathAndQuery)
            .FirstOrDefault();

    private static async Task<HttpResponseMessage> Get(
        WebApplication app, string route, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        return await app.GetTestClient().SendAsync(request);
    }
}
