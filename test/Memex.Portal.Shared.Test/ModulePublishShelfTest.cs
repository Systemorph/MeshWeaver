using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PackagingManifest = MeshWeaver.Plugin.Packaging.PluginManifest;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The registry SHELF over the publish route (2026-08-22): a module whose declared platform floor
/// exceeds the registry's own version is ACCEPTED, never 409'd — and since #3648 not even held:
/// the floor is advisory, and what the shelf holds is what the link probe measures as unloadable
/// here (<see cref="ModulePlatformLinkTest.AnUnloadableModule_IsShelvedRatherThanRefused_OnThePublishPath"/>).
///
/// <para><b>The deadlock the old behaviour produced (2026-08-22).</b> Modules extracted from the
/// platform image declared <c>minMeshVersion: 3.0.0-rc7</c> while the registry ran rc6, so the
/// publish endpoint answered every upload 409 — and the registry could not update to rc7 because
/// its <c>Modules:Required</c> gate held the rollout for exactly those absent modules. Image
/// doesn't ship them → only the registry can deliver them → the registry refuses to CARRY them
/// until it updates → it can't update without them. The shelf semantics break the cycle: the
/// warehouse carries modules for platforms newer than itself and serves them to consumers, whose
/// own landing measures them against THEIR platform.</para>
///
/// <para>Driven over a real HTTP pipeline (TestServer) like <see cref="PluginBundleAuthTest"/>,
/// because the claim under test is the ROUTE's answer — a publisher must be able to tell
/// "shelved, will serve" (200, <c>held: true</c>) apart from "activated here" (200,
/// <c>held: false</c>) and from a real refusal (409).</para>
/// </summary>
public class ModulePublishShelfTest : IDisposable
{
    private const string Token = "shelf-test-token";

    private const string Module = "MeshWeaver.Speech";

    /// <summary>The module-relative asset every bundle here carries. Its BODY is what makes two
    /// uploads differ in content — the generation leaf is a SHA-256 content address (#3656), so
    /// without it a "second" publish would resolve to the first one's directory and every
    /// head-pointer claim below would be vacuous.</summary>
    private const string MarkerAsset = "wwwroot/build.txt";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-shelf-" + Guid.NewGuid().ToString("N"));

    private WebApplication? registry;

    /// <summary>Creates the per-test landing root the shelf writes into.</summary>
    public ModulePublishShelfTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        (registry as IDisposable)?.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    /// <summary>A packed module bundle the route can read, declaring the given floor. A null
    /// <paramref name="frameworkMvid"/> omits the field entirely — the shape a producer on a lane
    /// older than #3211 uploads, and the one the registry refuses since #3240.</summary>
    /// <param name="minMeshVersion">The module's declared platform floor — advisory since #3648.</param>
    /// <param name="frameworkMvid">The producer's framework identity.</param>
    /// <param name="version">The package version this bundle is packed at — what the head rule of
    /// #3996 orders uploads by.</param>
    /// <param name="content">The marker asset's body, i.e. what makes these bytes distinct.
    /// Defaults to <paramref name="version"/>, so two versions are automatically two
    /// generations; pass it explicitly to publish DIFFERENT bytes at the SAME version.</param>
    private static byte[] Bundle(
        string? minMeshVersion,
        string? frameworkMvid = "test-build",
        string version = "1.0.0",
        string? content = null)
    {
        var body = content ?? version;
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "SpeechPkg",
            version,
            frameworkMvid,
            module = new
            {
                assemblyName = Module,
                assemblies = new[] { Module + ".dll" },
                minMeshVersion,
                staticAssets = new[] { MarkerAsset },
            },
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(
            buffer,
            new PackagingManifest("SpeechPkg", "MeshWeaver.Plugin.SpeechPkg", version, "SpeechPkg", null, []),
            "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    NuGetPackageWriter.ModuleEntryPathFor(Module + ".dll"),
                    // 🚨 REAL assembly bytes since #3538: the landing MEASURES the module's link
                    // requirements against this platform, so a short literal is refused as
                    // unreadable metadata — correctly, and these tests are about the FLOOR.
                    () => new MemoryStream(
                        File.ReadAllBytes(typeof(BundleReader).Assembly.Location))),
                new NuGetPackageWriter.Entry(
                    NuGetPackageWriter.ModuleAssetEntryPathFor(MarkerAsset),
                    () => new MemoryStream(Encoding.UTF8.GetBytes(body))),
            ],
            manifestJson);
        return buffer.ToArray();
    }

    /// <summary>
    /// The registry host, created once per test and REUSED across publishes — two uploads of one
    /// module are the whole subject of the #3996 controls, and they have to reach the same landing
    /// root through the same route the CI publishers use.
    /// </summary>
    private async Task<HttpClient> Registry()
    {
        if (registry is null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            // The publish route is mapped only when a token is configured; the shelf lands into this
            // test's own temp root, never the testhost's bin (the sidecar is a persistent file).
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ModulePublish.TokenConfigKey] = Token,
            });
            builder.Services.AddSingleton(new ModuleLandingService(baseDirectory: root));

            registry = builder.Build();
            registry.MapPluginBundles();
            await registry.StartAsync();
        }

        return registry.GetTestClient();
    }

    private async Task<HttpResponseMessage> Publish(
        string? minMeshVersion,
        string? frameworkMvid = "test-build",
        string version = "1.0.0",
        string? content = null)
    {
        var client = await Registry();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            PluginBundleEndpoints.RoutePrefix + "/SpeechPkg?packagePath=Plugins/SpeechPkg");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        request.Content = new ByteArrayContent(Bundle(minMeshVersion, frameworkMvid, version, content));
        return await client.SendAsync(request);
    }

    /// <summary>The publish route's JSON body, as the publishing CI job reads it.</summary>
    private static async Task<JsonElement> Body(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>The marker body inside one landed generation — WHICH bytes a generation holds,
    /// read off the disk rather than inferred from the version the record claims.</summary>
    private string MarkerIn(ModuleActivationEntry entry) =>
        File.ReadAllText(Path.Combine(
            ModuleLandingService.ModuleDirectoryFor(root, Module, entry),
            MarkerAsset.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The marker body the SERVE side hands a consumer — the same
    /// <see cref="ModuleBundleSource.Collect"/> the index and the download route resolve through,
    /// so the claim is about what this registry actually serves, not about a record.</summary>
    private string ServedMarker(ModuleActivationList activation, string? version = null)
    {
        var (_, assets, decline) = ModuleBundleSource.CollectVersion(root, Module, activation, version);
        Assert.Null(decline);
        var asset = Assert.Single(assets, a => a.RelativePath == MarkerAsset);
        return File.ReadAllText(asset.FullPath);
    }

    /// <summary>
    /// The fallback slot keeps the BETTER older generation too: a still OLDER late upload must not
    /// push 1.6.1 out from behind the 1.7.0 head — otherwise repeated lagging publications would keep
    /// the head and quietly walk its fallback backwards. The losing upload is not retained, and the
    /// response has to say so: calling it "shelved" would be a claim the modules GC falsifies five
    /// minutes later.
    /// </summary>
    [Fact]
    public async Task AStillOlderPublish_DoesNotMoveTheRetainedFallbackBackwards()
    {
        using (var newest = await Publish(minMeshVersion: null, version: "1.7.0"))
            Assert.Equal(HttpStatusCode.OK, newest.StatusCode);
        using (var fallback = await Publish(minMeshVersion: null, version: "1.6.1"))
            Assert.True((await Body(fallback)).GetProperty("retainedAsFallback").GetBoolean(),
                "the first older upload behind a head takes the empty fallback slot");

        using var oldest = await Publish(minMeshVersion: null, version: "1.5.0");

        Assert.Equal(HttpStatusCode.OK, oldest.StatusCode);
        var body = await Body(oldest);
        Assert.True(body.GetProperty("shelfOnly").GetBoolean());
        Assert.False(body.GetProperty("retainedAsFallback").GetBoolean(),
            "1.6.1 is the better fallback, so 1.5.0 is not kept");
        Assert.Contains("NOT retained", body.GetProperty("shelfOnlyReason").GetString()!,
            StringComparison.Ordinal);

        var head = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal("1.7.0", head.Version);
        Assert.Equal("1.7.0", MarkerIn(head));
        Assert.Equal("1.6.1", head.PreviousVersion);
        Assert.Equal("1.6.1", MarkerIn(ModuleActivationBoot.PreviousGeneration(head)!));
    }

    /// <summary>
    /// A higher VERSION string does not protect a head whose bytes are GONE. Only a landed
    /// generation is protected: a record naming a directory without its entry DLL is not a version
    /// this registry holds, and refusing the one valid upload that could heal it would turn the
    /// anti-rollback rule into a self-sealing outage.
    /// </summary>
    [Fact]
    public async Task AnOlderPublish_ReplacesANewerHeadWhoseBytesAreMissing()
    {
        using (var newer = await Publish(minMeshVersion: null, version: "1.7.0"))
            Assert.Equal(HttpStatusCode.OK, newer.StatusCode);
        var broken = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        File.Delete(ModuleActivationBoot.LandedDllPath(root, broken));
        Assert.False(ModuleActivationBoot.LandedModuleDllExists(root, broken));

        using var healing = await Publish(minMeshVersion: null, version: "1.6.1");

        Assert.Equal(HttpStatusCode.OK, healing.StatusCode);
        var body = await Body(healing);
        Assert.False(body.GetProperty("shelfOnly").GetBoolean(),
            "a head whose entry DLL is gone is no version this registry holds — the valid upload heals it");
        Assert.Equal("1.6.1", body.GetProperty("headVersion").GetString());

        var list = ModuleActivationSidecar.Read(root);
        var head = Assert.Single(list.Entries);
        Assert.Equal("1.6.1", head.Version);
        Assert.Equal("1.6.1", MarkerIn(head));
        Assert.Equal("1.6.1", ServedMarker(list));
    }

    /// <summary>
    /// 🚨 The SAME-BYTES case of a lost head (#4031 review). The generation directory is a CONTENT
    /// address that ignores the version label, so re-publishing the head's own bytes resolves to the
    /// very directory that lost its entry DLL — and the landing used to adopt that directory as-is,
    /// recording a broken head and changing nothing. The files it lost are restored from the
    /// identical staged copy, and the head keeps its higher label.
    /// </summary>
    [Fact]
    public async Task ARepublishOfTheHeadsOwnBytes_RestoresTheFilesItsGenerationLost()
    {
        using (var first = await Publish(minMeshVersion: null, version: "1.7.0", content: "same-bytes"))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var broken = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        File.Delete(ModuleActivationBoot.LandedDllPath(root, broken));
        Assert.False(ModuleActivationBoot.LandedModuleDllExists(root, broken));

        using var again = await Publish(minMeshVersion: null, version: "1.6.1", content: "same-bytes");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var list = ModuleActivationSidecar.Read(root);
        var head = Assert.Single(list.Entries);
        Assert.Equal(broken.Directory, head.Directory);
        Assert.True(ModuleActivationBoot.LandedModuleDllExists(root, head),
            "the re-published identical bytes restore the entry DLL the head generation lost");
        Assert.Equal("1.7.0", head.Version);
        Assert.Equal("same-bytes", ServedMarker(list));
    }

    /// <summary>
    /// 🚨 <b>An above-floor publish is neither 409'd nor HELD (#3648)</b> — it lands, unheld, with
    /// the floor recorded as an advisory. The upload that used to 409 into the 2026-08-22 deadlock
    /// answered <c>held: true</c> from then until #3648; on 2026-09-07 that same string comparison
    /// held every production portal on its morning build. The declared floor decides nothing now:
    /// the bytes LINK against this platform (real assembly bytes, measured by the landing), so
    /// they land exactly as a floor-satisfied publish does — PendingRestart raised, entry enabled,
    /// the floor on the record for the index and the status row to SAY, never to gate on.
    /// </summary>
    [Fact]
    public async Task AnAboveFloorPublish_LandsUnheld_TheFloorIsAdvisory()
    {
        // The precondition that makes this able to fail: 999.0.0 ranks ABOVE whatever this test
        // process runs, so the old gate would have held it. A comparator that stopped saying so
        // would turn every assertion below into a vacuous pass.
        Assert.NotNull(ModulePlatformFloor.DeclineReason("999.0.0"));

        using var response = await Publish(minMeshVersion: "999.0.0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("held").GetBoolean(),
            "a declared floor above the running platform is advisory — the bytes link, so nothing holds them");
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("holdReason").ValueKind);
        Assert.True(body.RootElement.GetProperty("pendingRestart").GetBoolean(),
            "an unheld landing loads at the next restart, so the restart IS pending");

        // The state on disk: bytes in a generation directory, entry recorded WITH its floor (the
        // index and the status row say what the module claims), restart flag raised.
        var list = ModuleActivationSidecar.Read(root);
        var entry = Assert.Single(list.Entries);
        Assert.Equal("MeshWeaver.Speech", entry.Name);
        Assert.True(entry.Enabled);
        Assert.Equal("999.0.0", entry.MinMeshVersion);
        // 🚨 The PRODUCER's framework identity survives the publish, verbatim (Plugins#931). The
        // index projects exactly this per bundle, and a consumer compares it against what it has
        // landed to tell a rebuild of unchanged source from a no-op — a rebuild republishes under
        // the SAME version, so if the shelf drops the identity here the whole comparison downstream
        // silently reads "unknown" and the defect is back.
        Assert.Equal("test-build", entry.FrameworkMvid);
        Assert.True(list.PendingRestart);
        Assert.True(File.Exists(Path.Combine(
            ModuleLandingService.ModuleDirectoryFor(root, "MeshWeaver.Speech", entry),
            "MeshWeaver.Speech.dll")));

        // …and the SERVE side lists exactly this landing for consumers — the same Collect the
        // index and the download route resolve through, against the state the route wrote.
        var (files, _, decline) = ModuleBundleSource.Collect(root, "MeshWeaver.Speech", list);
        Assert.Null(decline);
        Assert.Single(files);
    }

    /// <summary>A publish whose floor this platform satisfies is the unchanged behaviour:
    /// activated here (restart-as-activation), not held.</summary>
    [Fact]
    public async Task AFloorSatisfiedPublish_LandsAsBefore()
    {
        using var response = await Publish(minMeshVersion: "0.0.1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("held").GetBoolean());
        Assert.True(body.RootElement.GetProperty("pendingRestart").GetBoolean());

        var list = ModuleActivationSidecar.Read(root);
        Assert.True(list.PendingRestart, "an activated landing loads at the next restart");
        Assert.Equal("0.0.1", Assert.Single(list.Entries).MinMeshVersion);
    }

    /// <summary>
    /// 🚨 #3240 — THE REGISTRY'S OWN 400, armed 2026-09-05. A bundle that states no framework
    /// identity is REFUSED, by name, instead of shelving a null that the index then advertises.
    ///
    /// <para>Why the registry checks something its producers already refuse: a null on the SERVED
    /// side is the one unknown landing cannot heal. <c>ModuleUpdateDecision</c> (#3154) compares
    /// (version, framework identity) on every reconcile of every installation, and a module's
    /// version encodes CONTENT only — so a rebuild of unchanged source against a new platform
    /// republishes under the SAME version and the identity is the only thing that distinguishes it
    /// from a no-op. Shelve one null and every consumer of that module answers "already landed, the
    /// identity could not be checked" forever. A registry that trusts its publishers is not a
    /// registry.</para>
    ///
    /// <para>The refusal must stay shape-AGNOSTIC — `g&lt;sha&gt;`, a 32-hex MVID and `s&lt;hash&gt;`
    /// are all identities a lane legitimately states. Only ABSENCE is refused, which is what
    /// <see cref="AStatedIdentityOfAnyShape_IsAccepted"/> pins from the other side.</para>
    /// </summary>
    [Fact]
    public async Task ABundleStatingNoFrameworkIdentity_IsRefused_AndNothingIsShelved()
    {
        using var response = await Publish(minMeshVersion: null, frameworkMvid: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = body.RootElement.GetProperty("error").GetString();
        Assert.Contains("states no framework identity", error);
        // The remedy is named, not merely the fault — the publisher has to know what to change.
        Assert.Contains("node-repo-module-pack.yml", error);

        // 🚨 NOTHING was shelved. A refusal that still wrote the bytes would leave the null on the
        // shelf and only change the status code the publisher sees.
        Assert.Empty(ModuleActivationSidecar.Read(root).Entries);
    }

    /// <summary>
    /// The other side of the same refusal: an identity of a shape the from-source lane produces (a
    /// bare 32-hex MVID) is accepted and survives verbatim. Without this, narrowing the check to
    /// one token shape would look like a passing test while redding a lane that states its identity
    /// perfectly well.
    /// </summary>
    [Fact]
    public async Task AStatedIdentityOfAnyShape_IsAccepted()
    {
        const string mvid = "cef92e9759e940ea8a1aa73173b12227";
        using var response = await Publish(minMeshVersion: null, frameworkMvid: mvid);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal(mvid, entry.FrameworkMvid);
    }

    // ─────────────────── #3996: the head is a VERSION order, never an arrival order ───────────────────

    /// <summary>
    /// 🚨 <b>THE DEFECT (#3996).</b> The publish route moved the head pointer for every accepted
    /// upload, so which version a registry SERVED — and which one its own next restart loaded —
    /// was decided by CI queue timing. Measured on memex.meshweaver.cloud 2026-09-11:
    /// <c>MeshWeaver.Mail.MicrosoftGraph</c> 1.7.0 landed at 02:49Z and 1.6.1 displaced it at
    /// 03:00Z; the 03:30Z activation entry read <c>Version=1.6.1 PreviousVersion=1.7.0</c>, so the
    /// next restart would have silently un-shipped a merged change. Two lanes publish the same
    /// module (#3461) and core CD runs take 40–50 minutes, so the older gate routinely finished
    /// last.
    ///
    /// <para>The older upload is still ACCEPTED and still SHELVED — its bytes are on the volume and
    /// its generation is the entry's fallback, which is what keeps the modules GC off it. An upload
    /// that merely failed to become head and was recorded NOWHERE would be reclaimed five minutes
    /// later, trading one silent loss for another, so the GC pass below is part of the claim rather
    /// than decoration.</para>
    /// </summary>
    [Fact]
    public async Task AnOlderUpload_ArrivingAfterANewerOne_DoesNotBecomeTheHead_AndIsStillShelvedAndResolvable()
    {
        using (var newer = await Publish(minMeshVersion: null, version: "1.7.0"))
        {
            Assert.Equal(HttpStatusCode.OK, newer.StatusCode);
            Assert.False((await Body(newer)).GetProperty("shelfOnly").GetBoolean(),
                "the FIRST publish of a module has no head to rank below — it IS the head");
        }

        var head = Assert.Single(ModuleActivationSidecar.Read(root).Entries).Directory;

        using var older = await Publish(minMeshVersion: null, version: "1.6.1");

        // 🚨 200, never 409: the publisher did its job — these bytes ARE on the shelf. The reason
        // they are not the head is the other lane's timing, which no build job can see, and redding
        // a repo's CI for it would be a band-aid pointed at the wrong repo.
        Assert.Equal(HttpStatusCode.OK, older.StatusCode);
        var body = await Body(older);
        Assert.True(body.GetProperty("shelfOnly").GetBoolean(),
            "an upload ranking below the landed head is shelved WITHOUT becoming the head");
        var reason = body.GetProperty("shelfOnlyReason").GetString();
        Assert.Contains("1.6.1", reason!, StringComparison.Ordinal);
        Assert.Contains("1.7.0", reason!, StringComparison.Ordinal);
        Assert.False(body.GetProperty("held").GetBoolean(),
            "shelf-only is not the link probe's hold — these bytes load here perfectly well");
        Assert.False(body.GetProperty("pendingRestart").GetBoolean(),
            "the head did not move, so a restart would load exactly what is already running — "
            + "'restart required' would be a prompt no restart can clear");
        Assert.Equal("1.7.0", body.GetProperty("headVersion").GetString());
        Assert.True(body.GetProperty("retainedAsFallback").GetBoolean(),
            "the fallback slot was empty, so the older upload takes it");

        var list = ModuleActivationSidecar.Read(root);
        var entry = Assert.Single(list.Entries);

        // ── the head did NOT regress ──────────────────────────────────────────────────────────
        Assert.Equal("1.7.0", entry.Version);
        Assert.Equal(head, entry.Directory);
        Assert.Equal("1.7.0", MarkerIn(entry));
        Assert.Equal("1.7.0", ServedMarker(list));

        // ── and the older upload is STILL SHELVED, and RESOLVABLE ─────────────────────────────
        Assert.Equal("1.6.1", entry.PreviousVersion);
        var previous = ModuleActivationBoot.PreviousGeneration(entry);
        Assert.NotNull(previous);
        Assert.True(ModuleActivationBoot.LandedModuleDllExists(root, previous!),
            "a shelf-only upload that cannot be resolved is not shelved, it is lost");
        Assert.Equal("1.6.1", MarkerIn(previous!));
        // …and a consumer asking for 1.6.1 BY VERSION is served 1.6.1's bytes, while the
        // unversioned read (and 1.7.0) still serve the head.
        Assert.Equal("1.6.1", ServedMarker(list, "1.6.1"));
        Assert.Equal("1.7.0", ServedMarker(list, "1.7.0"));

        // 🚨 Reachable means SURVIVES THE SWEEP. The modules GC reclaims every generation directory
        // no entry and no module set references, and the grace window is the only thing that would
        // otherwise hide the loss for five minutes — so the pass runs here with the window collapsed
        // and the clock pushed forward, exactly as the other generation-lifetime tests run it.
        ModuleLandingService.CollectGarbage(root, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));
        Assert.True(ModuleActivationBoot.LandedModuleDllExists(root, previous!),
            "the shelf-only generation is referenced as the entry's fallback, so GC must not reclaim it");
        Assert.True(ModuleActivationBoot.LandedModuleDllExists(root, entry),
            "and the head it did not displace is untouched");
    }

    /// <summary>
    /// 🚨 <b>THE POSITIVE CONTROL, and the test without which the one above is worthless.</b> "The
    /// head never moves" satisfies every assertion in
    /// <see cref="AnOlderUpload_ArrivingAfterANewerOne_DoesNotBecomeTheHead_AndIsStillShelvedAndResolvable"/>
    /// while breaking the registry completely — no module would ever be updated again. A genuinely
    /// NEWER upload must move the head, raise the restart, and become what the serve side hands out.
    ///
    /// <para>The two tests together state the fix in one sentence: whichever order the two lanes
    /// finish in, the registry converges on the SAME head — 1.7.0 — because the ordering is the
    /// artifacts' and not the build queue's.</para>
    /// </summary>
    [Fact]
    public async Task ANewerUpload_ArrivingAfterAnOlderOne_BecomesTheHead()
    {
        using (var older = await Publish(minMeshVersion: null, version: "1.6.1"))
            Assert.Equal(HttpStatusCode.OK, older.StatusCode);

        var displaced = Assert.Single(ModuleActivationSidecar.Read(root).Entries).Directory;

        using var newer = await Publish(minMeshVersion: null, version: "1.7.0");

        Assert.Equal(HttpStatusCode.OK, newer.StatusCode);
        var body = await Body(newer);
        Assert.False(body.GetProperty("shelfOnly").GetBoolean(),
            "a newer version MUST take the head — a rule that never moved it would pass the "
            + "older-arrives-last control and ship a registry that can never be updated again");
        Assert.Equal(JsonValueKind.Null, body.GetProperty("shelfOnlyReason").ValueKind);
        Assert.Equal("1.7.0", body.GetProperty("headVersion").GetString());
        Assert.False(body.GetProperty("retainedAsFallback").GetBoolean());
        Assert.True(body.GetProperty("pendingRestart").GetBoolean(),
            "the head moved, so this instance genuinely loads something else at its next restart");

        var list = ModuleActivationSidecar.Read(root);
        var entry = Assert.Single(list.Entries);
        Assert.Equal("1.7.0", entry.Version);
        Assert.NotEqual(displaced, entry.Directory);
        Assert.Equal("1.7.0", MarkerIn(entry));
        Assert.Equal("1.7.0", ServedMarker(list));
        Assert.True(list.PendingRestart);

        // #3649 is unchanged by #3996: the generation the landing displaced becomes the fallback.
        Assert.Equal(displaced, entry.PreviousDirectory);
        Assert.Equal("1.6.1", entry.PreviousVersion);
    }

    /// <summary>
    /// 🚨 The comparison is STRICTLY BELOW, never at-or-below — and this is the test that says why.
    /// A module's version encodes CONTENT only, so a rebuild of unchanged source against a new
    /// platform republishes under the SAME version (Plugins#931/#723); that artifact is exactly the
    /// one consumers in fallback are waiting for. Writing <c>&lt;= 0</c> instead of <c>&lt; 0</c>
    /// would leave a registry permanently unable to serve a rebuild, with nothing anywhere saying so.
    /// </summary>
    [Fact]
    public async Task ARebuildRepublishedAtTheSameVersion_StillBecomesTheHead()
    {
        using (var first = await Publish(minMeshVersion: null, version: "1.7.0", content: "built-against-A"))
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var before = Assert.Single(ModuleActivationSidecar.Read(root).Entries).Directory;

        using var rebuild = await Publish(minMeshVersion: null, version: "1.7.0", content: "built-against-B");

        Assert.Equal(HttpStatusCode.OK, rebuild.StatusCode);
        Assert.False((await Body(rebuild)).GetProperty("shelfOnly").GetBoolean(),
            "an equal version is not an older version — a rebuild for a new platform ships under "
            + "the same version and must reach the shelf's head");

        var list = ModuleActivationSidecar.Read(root);
        var entry = Assert.Single(list.Entries);
        Assert.NotEqual(before, entry.Directory);
        Assert.Equal("built-against-B", MarkerIn(entry));
        Assert.Equal("built-against-B", ServedMarker(list));
    }

    /// <summary>
    /// The ordering itself, stated as a table, through the SAME <see cref="NuGetVersionComparer"/>
    /// <c>ModuleUpdateDecision</c> uses for <see cref="ModuleUpdateAction.SkipOlder"/>. That
    /// identity is load-bearing rather than tidy: a registry whose head ranked by a different order
    /// than its consumers' would serve a version every one of them refuses as older, and nothing
    /// would ever converge.
    ///
    /// <para>The <c>ci.900</c> / <c>ci.3758</c> pair is the case <c>NuGetVersionComparer</c> exists
    /// for — as TEXT <c>"900"</c> sorts above <c>"3758"</c>, and a string comparison here would pin
    /// a registry to a build thousands of runs stale with nothing red anywhere.</para>
    /// </summary>
    [Theory]
    // second becomes the head
    [InlineData("1.6.1", "1.7.0", true)]
    [InlineData("1.7.0-rc1", "1.7.0", true)]            // the stable outranks the pre-release it follows
    [InlineData("3.0.0-ci.900", "3.0.0-ci.3758", true)] // numeric identifiers, not text
    // second is shelf-only
    [InlineData("1.7.0", "1.6.1", false)]
    [InlineData("1.7.0", "1.7.0-rc1", false)]           // a pre-release published after its stable
    [InlineData("3.0.0-ci.3758", "3.0.0-ci.900", false)]
    public async Task TheHeadFollowsSemVerOrder_NeverArrivalOrder(
        string first, string second, bool secondBecomesTheHead)
    {
        using (var one = await Publish(minMeshVersion: null, version: first))
            Assert.Equal(HttpStatusCode.OK, one.StatusCode);

        using var two = await Publish(minMeshVersion: null, version: second);

        Assert.Equal(HttpStatusCode.OK, two.StatusCode);
        Assert.Equal(
            !secondBecomesTheHead,
            (await Body(two)).GetProperty("shelfOnly").GetBoolean());

        var list = ModuleActivationSidecar.Read(root);
        var entry = Assert.Single(list.Entries);
        var head = secondBecomesTheHead ? second : first;
        Assert.Equal(head, entry.Version);
        Assert.Equal(head, MarkerIn(entry));
        Assert.Equal(head, ServedMarker(list));
    }
}
