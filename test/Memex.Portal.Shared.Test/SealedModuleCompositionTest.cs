using System.IO.Compression;
using System.Reactive.Linq;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Cli;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Fixture;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A composition reads ONE publication, or none</b> (MeshWeaver#3401).
///
/// <para>A consumer of a sealed publication does <b>N+1 reads</b> — the module-set index, then each
/// bundle that index names — of a directory the publisher unseals, rewrites and re-seals under it.
/// The window is roughly a minute and a half per target per publish, and the <c>plugins</c> prefix
/// has TWO writers (core CD's <c>plugins-bake</c> and the MeshWeaver.Plugins satellite's own
/// <c>publish-bake</c>), so they can land inside each other's.</para>
///
/// <para>#3409 gave the BUNDLE lane a generation and made the window answer 503 instead of 404. The
/// MODULE lane kept reading unpinned, in both of its consumers (<c>memex build plugin</c> here, and
/// <c>.github/scripts/compose-sealed-modules.sh</c>) — and a mix there is worse than a red: module
/// bytes from two publications are the mvid mismatch that DECLINES every NodeType assembly at
/// adoption ("built against mvid:…, live is mvid:…"), which is invisible until a portal boots.</para>
///
/// <para>The publisher below reseals between the first module fetch and the second — the exact
/// production interleave, made deterministic rather than raced. Unpinned, the composition ends up
/// holding one module from each publication and says nothing. Pinned, the server refuses the stale
/// read and the consumer re-reads the publication that now applies, so every module carries ONE
/// generation.</para>
/// </summary>
public class SealedModuleCompositionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Identity = "s0123456789abcdef0123456789abcdef";
    private const string Source = "plugins";

    /// <summary>
    /// The falsifiable assertion is that every composed module carries the SAME generation marker.
    /// Reading unpinned — <c>RegistryGet(http, moduleUrl, null, ct)</c>, which is what this lane did
    /// before this change — leaves <c>MeshWeaver.AI</c> on generation 1 and
    /// <c>MeshWeaver.Essentials</c> on generation 2, and nothing in any status code says so.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AResealBetweenTwoModuleFetches_IsRefusedAndReRead_NotSilentlyMixed()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-sealed-modules-" + Guid.NewGuid().ToString("N"));
        var extDir = Path.Combine(root, "ext");
        var dir = Path.Combine(root, Identity, Source);
        var modules = Path.Combine(dir, PublishedBundleCatalogue.ModulesDirectoryName);
        Directory.CreateDirectory(modules);
        var sentinel = Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName);
        try
        {
            Publish(dir, modules, sentinel, "gen1");

            var key = await RegisterInstance("composer", $"{Source}/*");

            // THE PUBLISHER, mid-composition: the first module has been fetched and written, and
            // the publication is replaced before the second is asked for. Everything from here on
            // belongs to a different publication — which is the whole point.
            var resealed = 0;
            await using var app = await StartHost(root, (ctx, next) =>
            {
                if (IsSecondModuleFetch(ctx) && Interlocked.Exchange(ref resealed, 1) == 0)
                    Publish(dir, modules, sentinel, "gen2");
                return next(ctx);
            });

            var log = new StringWriter();
            var composed = await new BuildPluginCommand(log, log).ComposeSealedModules(
                Client(app, key), Source, Identity, "AI Essentials", extDir, TestContext.Current.CancellationToken);

            Assert.Equal(1, resealed);                       // the interleave actually happened
            Assert.True(composed is not null, log.ToString());
            Assert.Equal(4, composed.Count);
            Assert.Contains("/ext/MeshWeaver.AI/MeshWeaver.AI.dll", composed);
            Assert.Contains("/ext/MeshWeaver.Essentials/MeshWeaver.Essentials.dll", composed);

            // 🚨 THE ASSERTION THAT FAILS WITHOUT THE PRECONDITION: one publication, or none.
            // Unpinned, MeshWeaver.AI comes back "gen1" and MeshWeaver.Essentials "gen2" — two
            // publications in one compile surface, exit 0, nothing said.
            Assert.Equal("gen2", StagedMarker(extDir, "MeshWeaver.AI"));
            Assert.Equal("gen2", StagedMarker(extDir, "MeshWeaver.Essentials"));

            // …and the consumer SAW the move rather than absorbing it, so an operator reading the
            // log knows which publication was finally read.
            Assert.Contains("was resealed while its module set was being composed", log.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// The bound is what keeps the re-read a READ and not a poll: a publisher that reseals under
    /// every attempt is a real condition, and the consumer must go RED naming itself rather than
    /// spin. It must also never hand back a half-composition — <c>null</c>, and an empty ext dir.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APublisherThatResealsUnderEveryAttempt_RefusesLoudly_AndStagesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-sealed-modules-storm-" + Guid.NewGuid().ToString("N"));
        var extDir = Path.Combine(root, "ext");
        var dir = Path.Combine(root, Identity, Source);
        var modules = Path.Combine(dir, PublishedBundleCatalogue.ModulesDirectoryName);
        Directory.CreateDirectory(modules);
        var sentinel = Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName);
        try
        {
            Publish(dir, modules, sentinel, "gen1");
            var key = await RegisterInstance("storm-composer", $"{Source}/*");

            var reseals = 0;
            await using var app = await StartHost(root, (ctx, next) =>
            {
                if (IsSecondModuleFetch(ctx))
                    Publish(dir, modules, sentinel, $"gen{Interlocked.Increment(ref reseals) + 1}");
                return next(ctx);
            });

            var log = new StringWriter();
            var composed = await new BuildPluginCommand(log, log).ComposeSealedModules(
                Client(app, key), Source, Identity, "AI Essentials", extDir, TestContext.Current.CancellationToken);

            Assert.Null(composed);
            Assert.Equal(BuildPluginCommand.MaxPublicationRestarts + 1, reseals);
            Assert.Contains("MeshWeaver#3401", log.ToString());
            // Nothing from a publication this composition refused may reach the compile surface.
            Assert.Empty(Directory.EnumerateDirectories(extDir));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>The publish lands BETWEEN the two module reads: the first bundle is already
    /// fetched and written when the second is asked for, which is precisely the N+1 window.</summary>
    private static bool IsSecondModuleFetch(HttpContext ctx) =>
        ctx.Request.Path.Value?.EndsWith("/modules/essentials.module.nupkg", StringComparison.Ordinal)
        == true;

    // ── the publisher, and what it publishes ──────────────────────────────────────────────────

    /// <summary>
    /// One publication of the <c>plugins</c> source: two module bundles carrying
    /// <paramref name="marker"/>, their index, and the completion sentinel written LAST — the same
    /// order <c>.github/scripts/publish-bake-bundles.sh</c> writes them in. The sentinel's write
    /// time is what moves the generation, so a reseal to an identical NAME list is still a
    /// different publication — which it is, because the bytes behind those names have changed.
    /// </summary>
    private static void Publish(string dir, string modules, string sentinel, string marker)
    {
        // Monotone, so a reseal within the same wall-clock second still moves the generation on a
        // filesystem whose timestamps are coarse. The publication genuinely moved; its token must.
        var previous = File.Exists(sentinel)
            ? File.GetLastWriteTimeUtc(sentinel)
            : DateTime.UtcNow.AddMinutes(-10);
        WriteBundle(Path.Combine(dir, "Store.zip"), "Store");
        WriteModule(Path.Combine(modules, "ai.module.nupkg"), "MeshWeaver.AI", marker);
        WriteModule(Path.Combine(modules, "essentials.module.nupkg"), "MeshWeaver.Essentials", marker);
        File.WriteAllText(
            Path.Combine(modules, PublishedBundleCatalogue.ModulesIndexFileName),
            "ai.module.nupkg\nessentials.module.nupkg\n");
        File.WriteAllText(sentinel, "Store.zip\n");
        File.SetLastWriteTimeUtc(sentinel, previous.AddSeconds(1));
    }

    private static void WriteBundle(string path, string plugin)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("meshweaver/manifest.json").Open());
        w.Write($"{{\"plugin\":\"{plugin}\"}}");
    }

    /// <summary>A module bundle in the layout <c>StageModuleBundle</c> reads: the manifest names
    /// the entry assembly, and <c>meshweaver/modules/&lt;name&gt;.dll</c> carries the marker this
    /// test reads back to tell the two publications apart.</summary>
    private static void WriteModule(string path, string assembly, string marker)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("meshweaver/manifest.json").Open()))
            w.Write($"{{\"module\":{{\"assemblyName\":\"{assembly}\"}}}}");
        using (var w = new StreamWriter(zip.CreateEntry($"meshweaver/modules/{assembly}.dll").Open()))
            w.Write(marker);
    }

    private static string StagedMarker(string extDir, string assembly) =>
        File.ReadAllText(Path.Combine(extDir, assembly, $"{assembly}.dll"));

    // ── the registry, and the caller ──────────────────────────────────────────────────────────

    private static HttpClient Client(WebApplication app, string key)
    {
        var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", key);
        return http;
    }

    private MeshWeaverInstanceService InstanceService(params string[] defaultGrants) =>
        new(Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(defaultGrants.Select((entry, i) =>
                    new KeyValuePair<string, string?>($"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:{i}", entry)))
                .Build());

    private Task<string> RegisterInstance(string instanceId, params string[] defaultGrants) =>
        InstanceService(defaultGrants)
            .Register("owner", "Owner", "owner@test.com", instanceId, instanceId)
            .Select(r => r.RawKey).FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await();

    private async Task<WebApplication> StartHost(
        string publishedRoot, Func<HttpContext, RequestDelegate, Task> publisher)
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
        app.Use(publisher);
        app.MapPluginBundles();
        await app.StartAsync();
        return app;
    }
}
