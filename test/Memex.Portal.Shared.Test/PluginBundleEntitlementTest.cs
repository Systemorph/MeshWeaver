using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>THE PER-PACKAGE ENTITLEMENT GATE ON THE BUNDLE ROUTES (#1772).</b>
///
/// <para><see cref="PluginBundleAuthTest"/> pins the AUTHENTICATION half — a caller without a valid
/// <c>mwi_</c> key gets 401. That half always worked. This fixture pins the half that did not: the
/// authenticated caller was written into <c>HttpContext.Items</c> and <b>never read back</b>, so any
/// registered instance could download every installed package's bundle — 900 CHF courses included —
/// while the route's own XML doc claimed per-package entitlement was enforced. An instance key is
/// provisioned to every registered installation; the population that could exercise this was
/// "everyone who ever registered", not "an attacker who breached something".</para>
///
/// <para><b>The grant path is REAL, end to end.</b> Instances are registered through
/// <see cref="MeshWeaverInstanceService.Register"/> (which mints the key and seeds
/// <c>PluginCatalog:DefaultGrants</c> into the admin-owned <c>PluginGrant</c> node in the Admin
/// partition), the install records are written by <see cref="PackageInstaller.Install"/>, and the
/// key is resolved by the production <see cref="InstanceRegistryAuthenticator"/> over a real HTTP
/// request. Nothing is mocked, so a regression anywhere on that path fails here.</para>
///
/// <para><b>Both directions, and the oracle.</b> A granted instance gets the bytes; an ungranted one
/// gets a refusal that is byte-identical to the answer for a package that does not exist —
/// <see cref="RefusalIsIndistinguishableFromNotFound"/> compares status, body AND content headers,
/// because the URL scheme is fully predictable and a distinguishable refusal is an inventory oracle
/// over the whole catalogue (the property <c>/api/content</c> established in #587).</para>
/// </summary>
public class PluginBundleEntitlementTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The package the consumer IS entitled to — installed from the platform source.</summary>
    private const string GrantedPackage = "BundleGranted";

    /// <summary>The paid package it is NOT entitled to — installed from a different source, exactly
    /// as a 900 CHF course sits beside the free platform repo on the real registry.</summary>
    private const string PaidPackage = "BundlePaid";

    /// <summary>A package id that was never installed at all — the not-found baseline the refusal
    /// must be indistinguishable from.</summary>
    private const string AbsentPackage = "BundleNeverInstalled";

    private const string PlatformSource = "Plugins";
    private const string PaidSource = "Education";
    private const string Version = "1.4.0";

    /// <summary>The consumer that holds <c>Plugins/*</c> — the shape a real install is registered
    /// with (the platform repo defaulted in, paid sources left admin-granted).</summary>
    private const string GrantedInstance = "bundle-consumer-granted";

    /// <summary>A consumer registered with NO defaults: it authenticates and is entitled to nothing.
    /// "Registering is identity, not entitlement."</summary>
    private const string UngrantedInstance = "bundle-consumer-ungranted";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    // ── the real registration + grant path ────────────────────────────────────────────────────

    private MeshWeaverInstanceService InstanceService(params string[] defaultGrants) =>
        new(Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(defaultGrants.Select((entry, i) =>
                    new KeyValuePair<string, string?>(
                        $"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:{i}", entry)))
                .Build());

    /// <summary>Registers an install and returns its raw instance key — the only time it is
    /// readable, exactly as a real registration hands it over once.</summary>
    private Task<string> RegisterInstance(string instanceId, params string[] defaultGrants) =>
        InstanceService(defaultGrants)
            .Register("bundle-owner", "Bundle Owner", "owner@test.com", instanceId, instanceId)
            .Select(r => r.RawKey)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .ToTask();

    /// <summary>Installs a package the production way, so the install record carries the same
    /// <c>Source</c> stamp the registry writes — the half of the grant pair that is not the id.</summary>
    private Task<InstallResult> InstallPackage(string id, string source) =>
        PackageInstaller.Install(
                Mesh,
                new PackageManifest
                {
                    Id = id,
                    Name = id,
                    Kind = PackageKind.Content,
                    TargetPartition = id,
                    SourceFolder = id,
                    Version = "1.0.0",
                    // What the bundle index keys its URL on — a record without it is not servable.
                    ReleasedVersion = Version,
                    // Stamped by the registry as it merges its sources (PluginRegistryEndpoints) or
                    // by the lister on a registry instance (InstanceAutoRegistrationService). It is
                    // what a PluginGrantEntry is matched against.
                    Source = source,
                },
                [new PackageFile($"{id}/Doc.md", $"# {id}")],
                "HEAD")
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(120))
            .ToTask();

    // ── the route, over a real HTTP pipeline ──────────────────────────────────────────────────

    /// <summary>
    /// The bundle routes on a TestServer wired to the REAL mesh: the production
    /// <see cref="InstanceRegistryAuthenticator"/> resolves the presented key against the instance
    /// and grant nodes this fixture actually wrote, and the handlers read this mesh's install records.
    /// </summary>
    private async Task<WebApplication> StartBundleHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(new InstanceRegistryAuthenticator(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>()));

        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> Get(WebApplication app, string route, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        return await app.GetTestClient().SendAsync(request);
    }

    private static string BundleRoute(string plugin) =>
        $"{PluginBundleEndpoints.RoutePrefix}/{plugin}/{Version}";

    private static string IndexRoute => PluginBundleEndpoints.RoutePrefix + "/index.json";

    /// <summary>The plugin ids an index response advertises.</summary>
    private static async Task<IReadOnlyList<string>> IndexedPlugins(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("bundles").EnumerateArray()
            .Select(b => b.GetProperty("plugin").GetString()!)
            .ToArray();
    }

    // ── the assertions ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 The whole defect, in one test: an instance that authenticates but was granted nothing must
    /// not receive an installed package's bundle — and an instance granted the PLATFORM source must
    /// not receive the PAID one that sits beside it.
    ///
    /// <para>Pre-fix both refusals were 200 with the archive attached, because
    /// <c>AuthenticatedInstance.Allows</c> was never called on these routes.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task OnlyGrantedPackagesAreServed()
    {
        await InstallPackage(GrantedPackage, PlatformSource);
        await InstallPackage(PaidPackage, PaidSource);
        var grantedKey = await RegisterInstance(GrantedInstance, $"{PlatformSource}/*");
        var ungrantedKey = await RegisterInstance(UngrantedInstance);

        var app = await StartBundleHost();
        await using var _ = app;

        // The entitled fetch still works — a gate that refuses everyone is not a fix.
        using var served = await Get(app, BundleRoute(GrantedPackage), grantedKey);
        served.StatusCode.Should().Be(HttpStatusCode.OK,
            "an instance granted Plugins/* must still get the platform package's bundle");
        (await served.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(0,
            "the served bundle is a real archive, not an empty 200");

        // Same URL, a key that was granted nothing.
        using var refusedWholesale = await Get(app, BundleRoute(GrantedPackage), ungrantedKey);
        refusedWholesale.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a registered instance with no grant is entitled to NOTHING — registering is identity, "
            + "not entitlement");

        // The sharper direction: a real grant, for a different source. This is the paid-content
        // boundary — Plugins/* must not sweep in a 900 CHF course from Education.
        using var refusedPaid = await Get(app, BundleRoute(PaidPackage), grantedKey);
        refusedPaid.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a grant is per (source, package) — holding the platform source must not confer the "
            + "paid source installed beside it");
    }

    /// <summary>
    /// 🚨 A refusal must be indistinguishable from "there is no such bundle" — status, body AND
    /// content headers.
    ///
    /// <para>The URL is <c>/{plugin}/{version}</c> and plugin ids are public knowledge, so any
    /// difference between "you may not have this" and "this does not exist" turns the route into an
    /// inventory oracle over the entire catalogue: an instance could enumerate every paid package
    /// this registry carries, and at which versions, without being entitled to one of them. This is
    /// the property <c>/api/content</c> established in #587 by making its refusal body byte-identical
    /// to its not-found body.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task RefusalIsIndistinguishableFromNotFound()
    {
        await InstallPackage(PaidPackage, PaidSource);
        var grantedKey = await RegisterInstance(GrantedInstance, $"{PlatformSource}/*");

        var app = await StartBundleHost();
        await using var _ = app;

        // Installed here, but not for this caller …
        using var refused = await Get(app, BundleRoute(PaidPackage), grantedKey);
        // … versus a package that genuinely does not exist on this instance.
        using var absent = await Get(app, BundleRoute(AbsentPackage), grantedKey);

        refused.StatusCode.Should().Be(absent.StatusCode,
            "the status must not reveal that the package exists");
        (await refused.Content.ReadAsByteArrayAsync())
            .Should().Equal(await absent.Content.ReadAsByteArrayAsync(),
                "the body must be byte-identical — a differing reason string IS the oracle");
        refused.Content.Headers.ContentType?.ToString()
            .Should().Be(absent.Content.Headers.ContentType?.ToString(),
                "a Content-Type present on one answer and not the other distinguishes them just as "
                + "well as a body would");
        refused.Content.Headers.ContentLength
            .Should().Be(absent.Content.Headers.ContentLength);
        refused.Headers.Select(h => h.Key).OrderBy(k => k, StringComparer.Ordinal)
            .Should().Equal(absent.Headers.Select(h => h.Key).OrderBy(k => k, StringComparer.Ordinal),
                "and neither may carry a header the other does not");
    }

    /// <summary>
    /// The index is scoped too — an ungranted package is not merely un-fetchable, it is not listed.
    ///
    /// <para>This is what makes the download refusal above non-informative: with the index filtered,
    /// "absent from your index" and "404 on fetch" agree, and neither confirms existence. An index
    /// that advertised the whole inventory would hand over exactly the catalogue the 404 withholds.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheIndexListsOnlyGrantedPackages()
    {
        await InstallPackage(GrantedPackage, PlatformSource);
        await InstallPackage(PaidPackage, PaidSource);
        var grantedKey = await RegisterInstance(GrantedInstance, $"{PlatformSource}/*");
        var ungrantedKey = await RegisterInstance(UngrantedInstance);

        var app = await StartBundleHost();
        await using var _ = app;

        using var granted = await Get(app, IndexRoute, grantedKey);
        granted.StatusCode.Should().Be(HttpStatusCode.OK);
        var advertised = await IndexedPlugins(granted);
        advertised.Should().Contain(GrantedPackage, "the granted package is servable to this caller");
        advertised.Should().NotContain(PaidPackage,
            "a caller must not be able to learn that an ungranted package is installed here");

        using var ungranted = await Get(app, IndexRoute, ungrantedKey);
        ungranted.StatusCode.Should().Be(HttpStatusCode.OK,
            "an entitled-to-nothing instance still gets a well-formed index — an error would itself "
            + "be a signal");
        (await IndexedPlugins(ungranted)).Should().BeEmpty(
            "and that index is indistinguishable from a registry with nothing installed");
    }
}
