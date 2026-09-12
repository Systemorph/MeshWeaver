using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Reactive.Linq;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MeshWeaver.Fixture;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The registry SERVES sealed publications — step 2 of the plugin build contract, the half that
/// lets a downstream repo INSTALL its upstream without an Azure identity, a checkout of the
/// upstream's source, or any secret beyond the instance key it already holds.
///
/// <para>Pins four things, each of which was a real gap on 2026-08-27:</para>
/// <list type="number">
///   <item>a caller with a whole-source grant gets the seal's list and each listed bundle;</item>
///   <item>a caller WITHOUT the grant is refused (403) — the fetch is scoped per source, so a
///     satellite that may fetch <c>plugins</c> cannot fetch a source it does not depend on;</item>
///   <item>an unsealed source directory is 404 — the route serves nothing the boot seeder would
///     itself refuse, so a consumer can never be handed a torn set;</item>
///   <item>a bundle present on disk but NOT listed by the seal is 404 — an unsealed file is not
///     part of the publication, whatever the filesystem says.</item>
/// </list>
/// </summary>
public class PrebuiltPublicationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Identity = "s0123456789abcdef0123456789abcdef";
    private const string Source = "plugins";
    private const string Granted = "granted-satellite";
    private const string Ungranted = "ungranted-satellite";

    [Fact(Timeout = 120_000)]
    public async Task ServesTheSealedSet_ToAWholeSourceGrant_AndRefusesEveryoneElse()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-prebuilt-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, Identity, Source);
        Directory.CreateDirectory(dir);
        try
        {
            WriteBundle(Path.Combine(dir, "Store.zip"), "Store");
            WriteBundle(Path.Combine(dir, "Edu.zip"), "Edu");
            WriteBundle(Path.Combine(dir, "Orphan.zip"), "Orphan");           // on disk, NOT sealed
            File.WriteAllText(Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName), "Store.zip\nEdu.zip\n");
            var torn = Path.Combine(root, Identity, "torn");                  // a directory with no seal
            Directory.CreateDirectory(torn);
            WriteBundle(Path.Combine(torn, "X.zip"), "X");

            var grantedKey = await RegisterInstance(Granted, $"{Source}/*");
            var ungrantedKey = await RegisterInstance(Ungranted);
            await using var app = await StartHost(root);

            // 1. the seal's list, exactly — the orphan is not in it
            var index = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/{Source}", grantedKey);
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            var body = await index.Content.ReadAsStringAsync();
            Assert.Contains("Store.zip", body); Assert.Contains("Edu.zip", body);
            Assert.DoesNotContain("Orphan.zip", body);

            // …and each listed bundle's bytes
            var store = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/{Source}/Store.zip", grantedKey);
            Assert.Equal(HttpStatusCode.OK, store.StatusCode);
            Assert.True((await store.Content.ReadAsByteArrayAsync()).Length > 0);

            // 2. no grant on the source → 403, never the bytes
            var refused = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/{Source}", ungrantedKey);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            // 3. unsealed directory → 503 + Retry-After, NOT 404 (#3401). The publisher removes the
            // sentinel before republishing and restores it last, so a caller here is looking at a
            // publication that exists and is being replaced — transient, and it must say so.
            var tornKey = await RegisterInstance("torn-reader", "torn/*");
            var tornResp = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/torn", tornKey);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, tornResp.StatusCode);
            Assert.Equal("30", tornResp.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0"));
            Assert.Contains("republished", await tornResp.Content.ReadAsStringAsync());

            // …and a source nothing was ever published under stays a permanent 404, so the two are
            // distinguishable — that distinction is the whole point.
            var absentKey = await RegisterInstance("absent-reader", "never-published/*");
            var absent = await Get(
                app, $"/api/plugins/bundles/prebuilt/{Identity}/never-published", absentKey);
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);

            // 4. present on disk but not sealed → 404
            var orphan = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/{Source}/Orphan.zip", grantedKey);
            Assert.Equal(HttpStatusCode.NotFound, orphan.StatusCode);

            // and a path segment that is not a bare name is refused before any disk read
            var walk = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/..%2F{Source}", grantedKey);
            Assert.NotEqual(HttpStatusCode.OK, walk.StatusCode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// 🚨 MeshWeaver#2698: a gate pinned to an identity composes the module bytes the publication
    /// was SEALED against, from the publication — never the registry's package endpoint, whose
    /// bytes are the module's own lane's last build. The registry therefore serves a sealed
    /// publication's module set beside its bundles: exactly the index's list (an unlisted file is
    /// not part of the set), 404 with a reason for a publication that predates module sealing
    /// (so a consumer fails RED naming the republish instead of composing something else), and an
    /// EMPTY set for a bake that composed nothing — distinguishable from "predates".
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ServesTheSealedModuleSet_AndRefusesAPublicationThatPredatesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-prebuilt-modules-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, Identity, Source);
        var modules = Path.Combine(dir, PublishedBundleCatalogue.ModulesDirectoryName);
        Directory.CreateDirectory(modules);
        try
        {
            WriteBundle(Path.Combine(dir, "Store.zip"), "Store");
            File.WriteAllText(Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName), "Store.zip\n");
            WriteBundle(Path.Combine(modules, "ai.module.nupkg"), "AI");
            WriteBundle(Path.Combine(modules, "orphan.module.nupkg"), "Orphan");     // on disk, NOT in the set
            File.WriteAllText(Path.Combine(modules, PublishedBundleCatalogue.ModulesIndexFileName), "ai.module.nupkg\n");

            var legacy = Path.Combine(root, Identity, "legacy");                       // sealed BEFORE module sealing
            Directory.CreateDirectory(legacy);
            WriteBundle(Path.Combine(legacy, "L.zip"), "L");
            File.WriteAllText(Path.Combine(legacy, ShippedPrebuiltBundles.CompletionSentinelFileName), "L.zip\n");

            var bare = Path.Combine(root, Identity, "bare");                           // sealed, composed nothing
            Directory.CreateDirectory(Path.Combine(bare, PublishedBundleCatalogue.ModulesDirectoryName));
            WriteBundle(Path.Combine(bare, "B.zip"), "B");
            File.WriteAllText(Path.Combine(bare, ShippedPrebuiltBundles.CompletionSentinelFileName), "B.zip\n");
            File.WriteAllText(Path.Combine(bare, PublishedBundleCatalogue.ModulesDirectoryName, PublishedBundleCatalogue.ModulesIndexFileName), "");

            var key = await RegisterInstance("module-reader", $"{Source}/*", "legacy/*", "bare/*");
            var ungrantedKey = await RegisterInstance("module-stranger");
            await using var app = await StartHost(root);

            // 1. the set is the index's list — the orphan on disk is not in it
            var index = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/{Source}/modules", key);
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            var body = await index.Content.ReadAsStringAsync();
            Assert.Contains("ai.module.nupkg", body);
            Assert.DoesNotContain("orphan", body);
            var ai = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/{Source}/modules/ai.module.nupkg", key);
            Assert.Equal(HttpStatusCode.OK, ai.StatusCode);
            Assert.True((await ai.Content.ReadAsByteArrayAsync()).Length > 0);
            var orphan = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/{Source}/modules/orphan.module.nupkg", key);
            Assert.Equal(HttpStatusCode.NotFound, orphan.StatusCode);

            // 2. the NodeType bundle index is untouched by the module set (and "modules" is not a bundle)
            var bundles = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/{Source}", key);
            Assert.Contains("Store.zip", await bundles.Content.ReadAsStringAsync());

            // 3. a publication that predates module sealing → 404 that SAYS so, never an empty set
            var predates = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/legacy/modules", key);
            Assert.Equal(HttpStatusCode.NotFound, predates.StatusCode);
            Assert.Contains("predates module sealing", await predates.Content.ReadAsStringAsync());

            // 4. a bake that composed nothing → 200 with an EMPTY set
            var empty = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/bare/modules", key);
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            Assert.Contains("\"modules\":[]", (await empty.Content.ReadAsStringAsync()).Replace(" ", ""));

            // 5. no grant → 403; a walking name → refused before any disk read
            var refused = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/{Source}/modules", ungrantedKey);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var walk = await Get(app, $"/api/plugins/bundles/prebuilt/{Identity}/{Source}/modules/..%2FStore.zip", key);
            Assert.NotEqual(HttpStatusCode.OK, walk.StatusCode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void WriteBundle(string path, string plugin)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("meshweaver/manifest.json").Open());
        w.Write($"{{\"plugin\":\"{plugin}\"}}");
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

    private async Task<WebApplication> StartHost(string publishedRoot, Action? beforePublicationRead = null)
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
        if (beforePublicationRead is not null)
            builder.Services.AddSingleton<ILoggerFactory>(new PublicationReadLoggerFactory(beforePublicationRead));
        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync();
        return app;
    }

    /// <summary>
    /// 🚨 #3401. A consumer reads the index and then fetches each bundle it names — N+1 reads of a
    /// directory the publisher may reseal underneath it. Without a generation the second half of
    /// that fetch silently mixes bytes from two publications, and nothing in any status code says
    /// so. With one, the server refuses the stale read and names both generations.
    ///
    /// <para>This is the case that no amount of retrying fixes and that the 2026-09-06
    /// Manufacturing red could not have revealed: there, the window was observed as a 404 because
    /// the seal was absent. Here the seal is present both times and only its CONTENT moved.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AResealBetweenTheIndexAndTheBundleIsRefused_NotSilentlyMixed()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-prebuilt-gen-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, Identity, Source);
        Directory.CreateDirectory(dir);
        try
        {
            WriteBundle(Path.Combine(dir, "Store.zip"), "Store");
            var sentinel = Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName);
            File.WriteAllText(sentinel, "Store.zip\n");

            var key = await RegisterInstance(Granted, $"{Source}/*");
            await using var app = await StartHost(root);
            var route = $"/api/plugins/bundles/prebuilt/{Identity}/{Source}";

            // 1. the index carries a generation, and the same value as its ETag
            var index = await Get(app, route, key);
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            var generation = JsonDocument.Parse(await index.Content.ReadAsStringAsync())
                .RootElement.GetProperty("generation").GetString();
            Assert.False(string.IsNullOrWhiteSpace(generation));
            Assert.Equal(generation, index.Headers.ETag?.Tag.Trim('"'));

            // 2. pinning the generation we actually read is accepted
            var fresh = await Get(app, $"{route}/Store.zip", key, generation);
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);

            // 3. the publisher reseals — same bundle NAME, new bytes. Nothing about the listing
            //    changes, which is exactly why a name-based check cannot see this.
            File.Delete(Path.Combine(dir, "Store.zip"));   // a republish REPLACES the bytes
            WriteBundle(Path.Combine(dir, "Store.zip"), "Store-v2");
            File.SetLastWriteTimeUtc(sentinel, File.GetLastWriteTimeUtc(sentinel).AddSeconds(1));

            var moved = await Get(app, $"{route}/Store.zip", key, generation);
            Assert.Equal(HttpStatusCode.PreconditionFailed, moved.StatusCode);
            var body = JsonDocument.Parse(await moved.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(generation, body.GetProperty("held").GetString());
            Assert.NotEqual(generation, body.GetProperty("current").GetString());

            // 4. …and re-reading the index hands the caller the generation that now applies
            var again = await Get(app, route, key);
            var current = JsonDocument.Parse(await again.Content.ReadAsStringAsync())
                .RootElement.GetProperty("generation").GetString();
            Assert.Equal(current, body.GetProperty("current").GetString());
            Assert.Equal(
                HttpStatusCode.OK, (await Get(app, $"{route}/Store.zip", key, current)).StatusCode);

            // 5. a caller that pins NOTHING keeps working — the precondition is opt-in, so a
            //    satellite on an older gate pin is unaffected by this change.
            Assert.Equal(HttpStatusCode.OK, (await Get(app, $"{route}/Store.zip", key)).StatusCode);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [HubFact]
    public async Task RemovedSealAndRemovedIdentity_HaveDistinctAnswersOnEveryPublicationRoute()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-prebuilt-removed-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, Identity, Source);
        Directory.CreateDirectory(directory);
        try
        {
            WriteBundle(Path.Combine(directory, "Store.zip"), "Store");
            var seal = Path.Combine(directory, ShippedPrebuiltBundles.CompletionSentinelFileName);
            File.WriteAllText(seal, "Store.zip\n");
            var key = await RegisterInstance(Granted, $"{Source}/*");
            await using var app = await StartHost(root);
            var route = $"/api/plugins/bundles/prebuilt/{Identity}/{Source}";
            Assert.Equal(HttpStatusCode.OK, (await Get(app, route, key)).StatusCode);

            File.Delete(seal);
            string[] suffixes = ["", "/Store.zip", "/modules", "/modules/ai.module.nupkg"];
            foreach (var suffix in suffixes)
            {
                using var response = await Get(app, route + suffix, key);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Equal("30", response.Headers.RetryAfter?.ToString());
            }

            Directory.Delete(Path.Combine(root, Identity), recursive: true);
            foreach (var suffix in suffixes)
            {
                using var response = await Get(app, route + suffix, key);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Null(response.Headers.RetryAfter);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("/modules", false)]
    [InlineData("/modules/ai.module.nupkg", false)]
    [InlineData("/modules", true)]
    [InlineData("/modules/ai.module.nupkg", true)]
    public async Task PublicationRemovedBetweenTheTwoModuleReads_PreservesItsStatus(
        string suffix, bool removeDirectory)
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-prebuilt-between-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, Identity, Source);
        Directory.CreateDirectory(directory);
        try
        {
            WriteBundle(Path.Combine(directory, "Store.zip"), "Store");
            var seal = Path.Combine(directory, ShippedPrebuiltBundles.CompletionSentinelFileName);
            File.WriteAllText(seal, "Store.zip\n");
            var key = await RegisterInstance(Granted, $"{Source}/*");
            var reads = 0;
            // Authentication obtains the first logger; each catalogue read obtains the next.
            // This existing boundary lets the real HTTP handler finish its first sealed read
            // before removal at the second catalogue read (third logger), with no production hook.
            await using var app = await StartHost(root, () =>
            {
                if (++reads != 3)
                    return;
                Assert.True(File.Exists(seal));
                if (removeDirectory)
                    Directory.Delete(Path.Combine(root, Identity), recursive: true);
                else
                    File.Delete(seal);
            });
            using var response = await Get(
                app, $"/api/plugins/bundles/prebuilt/{Identity}/{Source}" + suffix, key);
            Assert.Equal(3, reads);
            Assert.Equal(removeDirectory ? HttpStatusCode.NotFound : HttpStatusCode.ServiceUnavailable,
                response.StatusCode);
            if (removeDirectory)
                Assert.Null(response.Headers.RetryAfter);
            else
                Assert.Equal("30", response.Headers.RetryAfter?.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 🚨 #3876, the LAST unguarded open on these routes. The seal read lists a name and stats it
    /// present; <c>Results.File(path, …)</c> then resolves that path AGAIN when the result executes,
    /// after the handler has returned. The publisher replaces a publication several times an hour
    /// during a release and retention removes whole identity directories, so a file that went away
    /// in between threw <see cref="FileNotFoundException"/> (or
    /// <see cref="DirectoryNotFoundException"/> from a removed parent, which is what Linux raises)
    /// out of result execution to <c>ExceptionHandlerMiddleware</c> — a 500 for a condition this
    /// route has had a correct answer for since #3401. The open now happens in the handler, and the
    /// SAME discrimination decides: the source directory is still there ⇒ transient, 503 +
    /// <c>Retry-After</c>; the identity directory is gone ⇒ permanent, 404.
    ///
    /// <para>Three directions, because a fix that answered 404 for everything would pass a
    /// two-case test: the healthy publication must still serve its BYTES — the positive control,
    /// asserted first, on the very route the removal then hits.</para>
    /// </summary>
    /// <param name="suffix">The byte route under the publication.</param>
    /// <param name="openRead">Which <c>PluginBundleEndpoints</c> logger the open sits behind —
    /// authentication takes the first, each catalogue read the next, and the serve the last. It is
    /// asserted exactly, so an ordinal that drifts fails LOUDLY instead of removing nothing.</param>
    /// <param name="removeIdentity">Whether the identity directory goes, or only the bytes.</param>
    [Theory]
    [InlineData("/Store.zip", 3, false)]
    [InlineData("/Store.zip", 3, true)]
    [InlineData("/modules/ai.module.nupkg", 4, false)]
    [InlineData("/modules/ai.module.nupkg", 4, true)]
    public async Task BytesRemovedBetweenTheSealReadAndTheOpen_AreTransientOrNotFound_NeverA500(
        string suffix, int openRead, bool removeIdentity)
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-prebuilt-serve-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, Identity, Source);
        var modules = Path.Combine(directory, PublishedBundleCatalogue.ModulesDirectoryName);
        Directory.CreateDirectory(modules);
        try
        {
            WriteBundle(Path.Combine(directory, "Store.zip"), "Store");
            var seal = Path.Combine(directory, ShippedPrebuiltBundles.CompletionSentinelFileName);
            File.WriteAllText(seal, "Store.zip\n");
            WriteBundle(Path.Combine(modules, "ai.module.nupkg"), "AI");
            File.WriteAllText(
                Path.Combine(modules, PublishedBundleCatalogue.ModulesIndexFileName), "ai.module.nupkg\n");
            var bytes = Path.Combine(
                directory, suffix.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

            var key = await RegisterInstance(Granted, $"{Source}/*");
            var armed = false;
            var reads = 0;
            await using var app = await StartHost(root, () =>
            {
                if (!armed || ++reads != openRead)
                    return;
                Assert.True(File.Exists(seal),
                    "the seal must still be readable here — this test removes the BYTES after the "
                    + "seal read has listed them, which is the race; a seal already gone would be "
                    + "the older, already-fixed condition instead");
                if (removeIdentity)
                    Directory.Delete(Path.Combine(root, Identity), recursive: true);
                else
                    File.Delete(bytes);
            });
            var route = $"/api/plugins/bundles/prebuilt/{Identity}/{Source}";

            // POSITIVE CONTROL, on the same route the removal then hits: a healthy sealed
            // publication still serves its bytes, with no Retry-After.
            using (var healthy = await Get(app, route + suffix, key))
            {
                Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
                Assert.True((await healthy.Content.ReadAsByteArrayAsync()).Length > 0,
                    "a healthy sealed publication must serve the bundle's BYTES");
                Assert.Null(healthy.Headers.RetryAfter);
            }

            armed = true;
            using var response = await Get(app, route + suffix, key);
            Assert.Equal(openRead, reads);
            Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
            if (removeIdentity)
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Null(response.Headers.RetryAfter);
            }
            else
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Equal("30", response.Headers.RetryAfter?.ToString());
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* the test may have removed it */ }
        }
    }

    /// <summary>
    /// 🚨 #3876 on the module set's OWN seal. <c>File.Exists(modules/_index)</c> leading an
    /// unguarded <c>File.ReadAllLines(modules/_index)</c> is the same racing pair the
    /// <c>_complete</c> readers shed in #3877/#3885, and it threw the same unhandled exception onto
    /// the same two module routes. The open now decides, and the discrimination is the point: an
    /// index absent at the open needs the OPPOSITE answer depending on WHY.
    ///
    /// <para>Three directions. (1) The index is gone at the open and the seal has gone with it —
    /// the publisher unseals FIRST and retention unseals before it removes an identity, so this is
    /// a publication being replaced right now: <c>PublicationUnavailable</c>, which the HTTP
    /// readers already turn into 503 + <c>Retry-After</c> while the directory is there and 404 once
    /// it is not (<see cref="RemovedSealAndRemovedIdentity_HaveDistinctAnswersOnEveryPublicationRoute"/>
    /// pins that mapping). (2) The index was never there and the seal reads fine — the publication
    /// predates module sealing, which is PERMANENT and must stay a 404 that says to republish;
    /// collapsing either into the other is the defect. (3) A healthy publication still yields its
    /// module set.</para>
    /// </summary>
    /// <param name="removeDirectory">Whether the whole <c>modules</c> directory goes at the open
    /// (<see cref="DirectoryNotFoundException"/>) or only the index
    /// (<see cref="FileNotFoundException"/>) — a removed parent raises neither the same exception
    /// nor the same message, and both must classify.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AModuleIndexRemovedAtTheOpen_IsTransient_NotAPublicationThatPredatesModuleSealing(
        bool removeDirectory)
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-prebuilt-index-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, Identity, Source);
        var modules = Path.Combine(directory, PublishedBundleCatalogue.ModulesDirectoryName);
        Directory.CreateDirectory(modules);
        try
        {
            WriteBundle(Path.Combine(directory, "Store.zip"), "Store");
            var seal = Path.Combine(directory, ShippedPrebuiltBundles.CompletionSentinelFileName);
            File.WriteAllText(seal, "Store.zip\n");
            WriteBundle(Path.Combine(modules, "ai.module.nupkg"), "AI");
            var index = Path.Combine(modules, PublishedBundleCatalogue.ModulesIndexFileName);
            File.WriteAllText(index, "ai.module.nupkg\n");

            // POSITIVE CONTROL: healthy, so a reading that refused everything could not pass.
            var healthy = PublishedBundleCatalogue.SealedModulesOf(directory);
            Assert.Equal(["ai.module.nupkg"], healthy.Modules);
            Assert.False(healthy.PublicationUnavailable);

            // 1. gone AT THE OPEN, seal gone with it — the publisher's own order — is TRANSIENT.
            var removedAtTheOpen = PublishedBundleCatalogue.ReadModuleSet(directory, null, path =>
            {
                if (removeDirectory)
                    Directory.Delete(modules, recursive: true);
                else
                    File.Delete(index);
                File.Delete(seal);
                return File.ReadAllLines(path);
            });
            Assert.Null(removedAtTheOpen.Modules);
            Assert.True(removedAtTheOpen.PublicationUnavailable,
                "an index that was there for the seal read and gone at the open is a publication "
                + "being replaced — 503 + Retry-After, never 'republish the source'");
            Assert.Contains("being replaced right now", removedAtTheOpen.Refusal);

            // 2. never there, seal intact — PERMANENT, and it must keep saying so.
            File.WriteAllText(seal, "Store.zip\n");
            Directory.CreateDirectory(modules);
            var predates = PublishedBundleCatalogue.SealedModulesOf(directory);
            Assert.Null(predates.Modules);
            Assert.False(predates.PublicationUnavailable,
                "a sealed publication that simply carries no module set is a permanent 404 telling "
                + "the caller to republish — it must not wear the transient answer");
            Assert.Contains("predates module sealing", predates.Refusal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* the test may have removed it */ }
        }
    }

    private sealed class PublicationReadLoggerFactory(Action beforeRead) : ILoggerFactory
    {
        private readonly ILoggerFactory inner = LoggerFactory.Create(_ => { });

        public ILogger CreateLogger(string categoryName)
        {
            if (categoryName == typeof(PluginBundleEndpoints).FullName)
                beforeRead();
            return inner.CreateLogger(categoryName);
        }

        public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);
        public void Dispose() => inner.Dispose();
    }

    private static async Task<HttpResponseMessage> Get(
        WebApplication app, string route, string key, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await app.GetTestClient().SendAsync(request);
    }
}
