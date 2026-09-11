using System;
using System.Collections.Generic;
using System.IO;
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

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-shelf-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the per-test landing root the shelf writes into.</summary>
    public ModulePublishShelfTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    /// <summary>A packed module bundle the route can read, declaring the given floor. A null
    /// <paramref name="frameworkMvid"/> omits the field entirely — the shape a producer on a lane
    /// older than #3211 uploads, and the one the registry refuses since #3240.</summary>
    private static byte[] Bundle(
        string? minMeshVersion,
        string? frameworkMvid = "test-build",
        string version = "1.0.0",
        string? generationMarker = null)
    {
        const string markerPath = "wwwroot/generation.txt";
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "SpeechPkg",
            version,
            frameworkMvid,
            module = new
            {
                assemblyName = "MeshWeaver.Speech",
                assemblies = new[] { "MeshWeaver.Speech.dll" },
                minMeshVersion,
                staticAssets = generationMarker is null ? null : new[] { markerPath },
            },
        });

        var entries = new List<NuGetPackageWriter.Entry>
        {
            new(
                NuGetPackageWriter.ModuleEntryPathFor("MeshWeaver.Speech.dll"),
                // 🚨 REAL assembly bytes since #3538: the landing MEASURES the module's link
                // requirements against this platform, so a short literal is refused as
                // unreadable metadata — correctly, and these tests are about the FLOOR.
                () => new MemoryStream(
                    File.ReadAllBytes(typeof(BundleReader).Assembly.Location))),
        };
        if (generationMarker is not null)
            entries.Add(new NuGetPackageWriter.Entry(
                NuGetPackageWriter.ModuleAssetEntryPathFor(markerPath),
                () => new MemoryStream(Encoding.UTF8.GetBytes(generationMarker))));

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(
            buffer,
            new PackagingManifest("SpeechPkg", "MeshWeaver.Plugin.SpeechPkg", version, "SpeechPkg", null, []),
            "3.0.0",
            entries,
            manifestJson);
        return buffer.ToArray();
    }

    private async Task<HttpResponseMessage> Publish(
        string? minMeshVersion,
        string? frameworkMvid = "test-build",
        string version = "1.0.0",
        string? generationMarker = null)
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

        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync();

        var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            PluginBundleEndpoints.RoutePrefix + "/SpeechPkg?packagePath=Plugins/SpeechPkg");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        request.Content = new ByteArrayContent(
            Bundle(minMeshVersion, frameworkMvid, version, generationMarker));
        return await client.SendAsync(request);
    }

    /// <summary>
    /// 🚨 #3996 — an older publisher may STOCK its bytes after a newer publish, but it must
    /// never move the registry's activation head backwards. Core CD takes 40–50 minutes and used
    /// to arrive after Plugins' own publication: Mail 1.7.0 landed at 02:49Z, then the older
    /// core-gated 1.6.1 upload landed at 03:00Z and became the head solely because it arrived last.
    /// A restart consequently activated 1.6.1 and silently un-shipped the merge.
    ///
    /// <para>The two bundles deliberately carry different marker assets so they produce distinct
    /// content-addressed generations while sharing valid, linkable assembly bytes. The assertion
    /// is the durable activation record, not the endpoint's status code: both uploads have always
    /// answered 200.</para>
    /// </summary>
    [Fact]
    public async Task AnOlderPublishAfterANewerOne_DoesNotMoveTheActivationHeadBackwards()
    {
        using var newer = await Publish(
            minMeshVersion: null, version: "1.7.0", generationMarker: "newer");
        Assert.Equal(HttpStatusCode.OK, newer.StatusCode);

        using var older = await Publish(
            minMeshVersion: null, version: "1.6.1", generationMarker: "older");
        Assert.Equal(HttpStatusCode.OK, older.StatusCode);
        using var result = JsonDocument.Parse(await older.Content.ReadAsStringAsync());
        Assert.False(result.RootElement.GetProperty("selectedAsHead").GetBoolean());
        Assert.Equal("1.7.0", result.RootElement.GetProperty("headVersion").GetString());
        Assert.False(result.RootElement.GetProperty("pendingRestart").GetBoolean());

        var activation = ModuleActivationSidecar.Read(root);
        var head = Assert.Single(activation.Entries);
        Assert.Equal("1.7.0", head.Version);
        Assert.Equal("1.6.1", head.PreviousVersion);
        Assert.NotEqual(head.Directory, head.PreviousDirectory);
        Assert.True(ModuleActivationBoot.LandedModuleDllExists(root, head));
        Assert.True(ModuleActivationBoot.PreviousLandedModuleDllExists(root, head));

        var headMarker = Path.Combine(
            ModuleLandingService.ModuleDirectoryFor(root, head.Name, head),
            "wwwroot", "generation.txt");
        Assert.Equal("newer", File.ReadAllText(headMarker));
    }

    /// <summary>The shelf retains the newest useful fallback too: a still older late upload may
    /// not push 1.6.1 out from behind the 1.7.0 head. Otherwise repeated lagging publications
    /// would preserve the head but quietly walk its fallback backwards.</summary>
    [Fact]
    public async Task AStillOlderPublish_DoesNotMoveTheRetainedFallbackBackwards()
    {
        using var newest = await Publish(
            minMeshVersion: null, version: "1.7.0", generationMarker: "newest");
        using var fallback = await Publish(
            minMeshVersion: null, version: "1.6.1", generationMarker: "fallback");
        using var oldest = await Publish(
            minMeshVersion: null, version: "1.5.0", generationMarker: "oldest");
        Assert.Equal(HttpStatusCode.OK, newest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
        Assert.Equal(HttpStatusCode.OK, oldest.StatusCode);

        var head = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal("1.7.0", head.Version);
        Assert.Equal("1.6.1", head.PreviousVersion);
        var fallbackEntry = ModuleActivationBoot.PreviousGeneration(head);
        Assert.NotNull(fallbackEntry);
        var marker = Path.Combine(
            ModuleLandingService.ModuleDirectoryFor(root, head.Name, fallbackEntry),
            "wwwroot", "generation.txt");
        Assert.Equal("fallback", File.ReadAllText(marker));
    }

    /// <summary>A higher VERSION string is not enough to protect a broken head. If its entry DLL
    /// is absent, the next valid upload must heal the shelf even when its version is lower; keeping
    /// the unusable record would turn the anti-rollback rule into a self-sealing outage.</summary>
    [Fact]
    public async Task AnOlderPublish_ReplacesANewerHeadWhoseBytesAreMissing()
    {
        using var newer = await Publish(
            minMeshVersion: null, version: "1.7.0", generationMarker: "newer-but-broken");
        Assert.Equal(HttpStatusCode.OK, newer.StatusCode);
        var broken = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        File.Delete(ModuleActivationBoot.LandedDllPath(root, broken));
        Assert.False(ModuleActivationBoot.LandedModuleDllExists(root, broken));

        using var healing = await Publish(
            minMeshVersion: null, version: "1.6.1", generationMarker: "healing");
        Assert.Equal(HttpStatusCode.OK, healing.StatusCode);
        using var result = JsonDocument.Parse(await healing.Content.ReadAsStringAsync());
        Assert.True(result.RootElement.GetProperty("selectedAsHead").GetBoolean());
        Assert.Equal("1.6.1", result.RootElement.GetProperty("headVersion").GetString());

        var head = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal("1.6.1", head.Version);
        Assert.True(ModuleActivationBoot.LandedModuleDllExists(root, head));
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
}
