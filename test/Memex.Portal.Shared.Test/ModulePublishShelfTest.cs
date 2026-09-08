using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
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
    private static byte[] Bundle(string? minMeshVersion, string? frameworkMvid = "test-build")
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = "SpeechPkg",
            version = "1.0.0",
            frameworkMvid,
            module = new
            {
                assemblyName = "MeshWeaver.Speech",
                assemblies = new[] { "MeshWeaver.Speech.dll" },
                minMeshVersion,
            },
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(
            buffer,
            new PackagingManifest("SpeechPkg", "MeshWeaver.Plugin.SpeechPkg", "1.0.0", "SpeechPkg", null, []),
            "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    NuGetPackageWriter.ModuleEntryPathFor("MeshWeaver.Speech.dll"),
                    // 🚨 REAL assembly bytes since #3538: the landing MEASURES the module's link
                    // requirements against this platform, so a short literal is refused as
                    // unreadable metadata — correctly, and these tests are about the FLOOR.
                    () => new MemoryStream(
                        File.ReadAllBytes(typeof(BundleReader).Assembly.Location))),
            ],
            manifestJson);
        return buffer.ToArray();
    }

    private async Task<HttpResponseMessage> Publish(
        string? minMeshVersion, string? frameworkMvid = "test-build")
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
        request.Content = new ByteArrayContent(Bundle(minMeshVersion, frameworkMvid));
        return await client.SendAsync(request);
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
