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
/// 🚨 <b>THE ENTITLEMENT ANCHOR, END TO END</b> (#1782 gap 2) — the registry answers, the local
/// install record is a cache, and its absence never denies.
///
/// <para><see cref="PluginBundleEntitlementTest"/> pins the gate itself (#1772/#1777): only granted
/// packages are served, and every refusal is byte-identical to "no such bundle". That fixture is
/// unchanged and still passes — this one pins the half it could not express, because before the
/// anchor there was nothing to express it WITH: the <c>(source, package)</c> binding came from the
/// install record and from nowhere else, so a package the serving instance had not itself installed
/// had nothing to match a grant against, and "I cannot tell which source this is from" came out as
/// "you are not entitled to it".</para>
///
/// <para><b>Everything below the anchor is REAL</b> — the same production path
/// <see cref="PluginBundleEntitlementTest"/> exercises: instances registered through
/// <see cref="MeshWeaverInstanceService.Register"/> (real minted key, real <c>DefaultGrants</c>
/// seed into the admin-owned grant node), install records written and removed by
/// <see cref="PackageInstaller"/>, the key resolved by the production
/// <see cref="InstanceRegistryAuthenticator"/> over a real HTTP request. Only the registry SOURCE is
/// a stub, and it has to be: the two states that matter most are a registry that carries a package
/// this instance never installed, and one that will not answer at all.</para>
/// </summary>
public class PluginBundleAnchorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The package whose install record is REMOVED — the cache, deleted — while the
    /// registry keeps carrying it.</summary>
    private const string AnchoredPackage = "BundleAnchored";

    /// <summary>The paid package beside it, carried by the registry in a source this caller does
    /// not hold.</summary>
    private const string PaidPackage = "BundleAnchoredPaid";

    /// <summary>Never installed, never advertised — the package nothing has ever observed.</summary>
    private const string UnknownPackage = "BundleNeverHeardOf";

    private const string PlatformSource = "Plugins";
    private const string PaidSource = "Education";
    private const string Version = "1.4.0";

    private const string GrantedInstance = "anchor-consumer-granted";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    // ── the real registration + install path (identical to PluginBundleEntitlementTest) ────────

    private Task<string> RegisterInstance(string instanceId, params string[] defaultGrants) =>
        new MeshWeaverInstanceService(
                Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>(),
                Mesh,
                Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(defaultGrants.Select((entry, i) =>
                        new KeyValuePair<string, string?>(
                            $"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:{i}", entry)))
                    .Build())
            .Register("anchor-owner", "Anchor Owner", "owner@test.com", instanceId, instanceId)
            .Select(r => r.RawKey)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .ToTask();

    private Task<InstallResult> InstallPackage(string id, string? source) =>
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
                    ReleasedVersion = Version,
                    Source = source,
                },
                [new PackageFile($"{id}/Doc.md", $"# {id}")],
                "HEAD")
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(120))
            .ToTask();

    /// <summary>Removes the install record through the installer's own sanctioned route — the
    /// CACHE, deleted, leaving the installed content and its partition exactly where they were.</summary>
    private Task<bool> RemoveInstallRecord(string id) =>
        PackageInstaller.RemoveInstalledRecord(Mesh, id)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .ToTask();

    // ── the host, with a stub ANCHOR ──────────────────────────────────────────────────────────

    private PackageEntitlementLedger ledger = new();

    /// <summary>
    /// The bundle routes on a TestServer wired to the REAL mesh, plus a stub registry anchor.
    ///
    /// <para>The anchor is registered on the REQUEST's services, which is exactly how a host
    /// overrides it in production shapes too (<c>PluginBundleEndpoints.Anchor</c> reads
    /// <c>RequestServices</c> first, then the mesh's — the same two-step
    /// <c>ModuleLandingService</c> uses).</para>
    /// </summary>
    private async Task<WebApplication> StartBundleHost(params ConfiguredPackageSource[] sources)
    {
        ledger = new PackageEntitlementLedger();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(new InstanceRegistryAuthenticator(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>()));
        builder.Services.AddSingleton(ledger);
        builder.Services.AddSingleton(new PackageOriginAnchor(
            () => sources,
            // No reuse window: every request re-reads, so a source that starts failing mid-test is
            // actually observed failing rather than answered from a snapshot.
            TimeSpan.Zero,
            () => DateTimeOffset.UtcNow,
            Mesh.ServiceProvider.GetRequiredService<ILogger<PackageOriginAnchor>>()));

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

    private static async Task<IReadOnlyList<string>> IndexedPlugins(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("bundles").EnumerateArray()
            .Select(b => b.GetProperty("plugin").GetString()!)
            .ToArray();
    }

    // ── the assertions ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 <b>THE ISSUE, end to end: an entitled caller with NO install record is not denied.</b>
    ///
    /// <para>The package is installed the production way and then its install RECORD is removed —
    /// the cache, deleted, while the content and its partition stay exactly where they are. That is
    /// the state a registry which provisions its packages as Spaces is permanently in (memex-cloud
    /// never runs the catalog install, so it has no install records at all), and it is the state a
    /// fresh instance is in for a package a customer has already paid for.</para>
    ///
    /// <para>Pre-fix the answer was 404 with the package absent from the index, because the grant
    /// had nothing to match against. It must now resolve upstream and serve.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnEntitledCallerWithNoInstallRecordIsStillServed()
    {
        await InstallPackage(AnchoredPackage, PlatformSource);
        (await RemoveInstallRecord(AnchoredPackage)).Should().BeTrue(
            "the test's premise is that the CACHE is gone");

        var key = await RegisterInstance(GrantedInstance, $"{PlatformSource}/*");

        var app = await StartBundleHost(Carrying(PlatformSource, AnchoredPackage));
        await using var _ = app;

        using var index = await Get(app, IndexRoute, key);
        index.StatusCode.Should().Be(HttpStatusCode.OK);
        (await IndexedPlugins(index)).Should().Contain(AnchoredPackage,
            "the registry carries this package and the caller's grant covers that source — the "
            + "absence of a local install record is not evidence of anything");

        using var served = await Get(app, BundleRoute(AnchoredPackage), key);
        served.StatusCode.Should().Be(HttpStatusCode.OK,
            "🚨 a purchase must not read as no purchase merely because nothing has installed here");

        ledger.Decisions.Should().Contain(
            d => d.PackageId == AnchoredPackage
                 && d.Outcome == EntitlementOutcome.Granted
                 && d.Anchor == EntitlementAnchorKind.Registry,
            "and the answer must say it came from the anchor, not from a cache");
    }

    /// <summary>
    /// 🚨 <b>An UNREACHABLE registry produces the third state, never a denial.</b>
    ///
    /// <para>Two halves, and they need different answers. A package whose entitlement was
    /// PREVIOUSLY OBSERVED here — the install record still carries its stamped source — keeps being
    /// served, from a decision that says out loud that it came from a cache. A package nothing has
    /// ever observed cannot be served (there is no answer to serve from), but it resolves
    /// <see cref="EntitlementOutcome.Indeterminate"/>: UNKNOWN, recorded, and never asserted as
    /// "not entitled".</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnUnreachableRegistryDoesNotDeny()
    {
        await InstallPackage(AnchoredPackage, PlatformSource);
        var key = await RegisterInstance(GrantedInstance, $"{PlatformSource}/*");

        var app = await StartBundleHost(Failing(PlatformSource, "the registry is down"));
        await using var _ = app;

        using var served = await Get(app, BundleRoute(AnchoredPackage), key);
        served.StatusCode.Should().Be(HttpStatusCode.OK,
            "fail toward not blocking a caller whose entitlement was previously observed — an "
            + "unreachable registry is not evidence of a missing purchase");
        ledger.Decisions.Should().Contain(
            d => d.PackageId == AnchoredPackage
                 && d.Outcome == EntitlementOutcome.Granted
                 && d.Anchor == EntitlementAnchorKind.Cache
                 && d.IsDegraded,
            "🚨 …and the cache must say it is a cache, or it silently becomes the anchor again");

        using var unknown = await Get(app, BundleRoute(UnknownPackage), key);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the bytes are withheld — the third state differs in what it CLAIMS, not in what it "
            + "hands over");
        ledger.Decisions.Should().Contain(
            d => d.PackageId == UnknownPackage && d.Outcome == EntitlementOutcome.Indeterminate,
            "🚨 and the recorded answer is UNKNOWN, not Denied: 'I could not find out' and 'you did "
            + "not buy it' must never be the same fact");
        ledger.Decisions.Should().NotContain(
            d => d.PackageId == UnknownPackage && d.Outcome == EntitlementOutcome.Denied);
        ledger.Describe().Should().Contain("UNKNOWN, not denials",
            "the degradation has to be legible rather than inferred from a quiet day");
    }

    /// <summary>
    /// 🚨 The deny direction, which must not be lost: a caller who is genuinely not entitled still
    /// sees nothing — including when the ANCHOR is the thing that carries the package.
    ///
    /// <para>This is the paid-content boundary the anchor could otherwise have widened: the
    /// registry advertises a package to everyone who can read its catalog, and holding
    /// <c>Plugins/*</c> must still not sweep in a 900 CHF course from <c>Education</c>.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ACallerWhoIsNotEntitledStillSeesNothing()
    {
        await InstallPackage(PaidPackage, PaidSource);
        var key = await RegisterInstance(GrantedInstance, $"{PlatformSource}/*");

        var app = await StartBundleHost(
            Carrying(PaidSource, PaidPackage), Carrying(PlatformSource, AnchoredPackage));
        await using var _ = app;

        using var index = await Get(app, IndexRoute, key);
        (await IndexedPlugins(index)).Should().NotContain(PaidPackage,
            "a caller must not even learn that an ungranted package is carried here");

        using var refused = await Get(app, BundleRoute(PaidPackage), key);
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ledger.Decisions.Should().Contain(
            d => d.PackageId == PaidPackage
                 && d.Outcome == EntitlementOutcome.Denied
                 && d.Anchor == EntitlementAnchorKind.Registry,
            "the anchor was consulted in full and the grant does not cover the source it names — "
            + "that is a real denial, and the rule must still be able to produce one");
    }

    /// <summary>
    /// #1777 is not weakened: with the anchor in play, a refusal is still byte-identical to the
    /// answer for a package that does not exist — status, body, content headers and header names.
    ///
    /// <para>The anchor adds STATES, never wire outcomes: the route still answers with the bytes or
    /// with the one <c>NoSuchBundle()</c>. Here the two refusals are reached by different internal
    /// paths on purpose — one Denied against the registry, one Indeterminate because nothing binds
    /// it — which is exactly the pair a distinguishable refusal would leak.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task TheThirdStateIsIndistinguishableOnTheWire()
    {
        await InstallPackage(PaidPackage, PaidSource);
        var key = await RegisterInstance(GrantedInstance, $"{PlatformSource}/*");

        var app = await StartBundleHost(Failing(PlatformSource, "the registry is down"));
        await using var _ = app;

        // Denied — a cached binding this caller's grant does not cover.
        using var denied = await Get(app, BundleRoute(PaidPackage), key);
        // Indeterminate — nothing binds it and the anchor cannot be asked.
        using var indeterminate = await Get(app, BundleRoute(UnknownPackage), key);

        denied.StatusCode.Should().Be(indeterminate.StatusCode);
        (await denied.Content.ReadAsByteArrayAsync())
            .Should().Equal(await indeterminate.Content.ReadAsByteArrayAsync(),
                "a body that revealed WHICH refusal this was would be the inventory oracle #1777 "
                + "closed, rebuilt out of the new states");
        denied.Content.Headers.ContentType?.ToString()
            .Should().Be(indeterminate.Content.Headers.ContentType?.ToString());
        denied.Content.Headers.ContentLength
            .Should().Be(indeterminate.Content.Headers.ContentLength);
        denied.Headers.Select(h => h.Key).OrderBy(k => k, StringComparer.Ordinal)
            .Should().Equal(
                indeterminate.Headers.Select(h => h.Key).OrderBy(k => k, StringComparer.Ordinal));

        // …and the distinction lives where the caller cannot read it.
        ledger.Decisions.Should().Contain(d => d.PackageId == PaidPackage
            && d.Outcome == EntitlementOutcome.Denied);
        ledger.Decisions.Should().Contain(d => d.PackageId == UnknownPackage
            && d.Outcome == EntitlementOutcome.Indeterminate);
    }

    // ── stub sources ──────────────────────────────────────────────────────────────────────────

    private static ConfiguredPackageSource Carrying(string source, params string[] packageIds) =>
        new(
            new StubSource(
                packageIds.Select(id => new PackageManifest
                {
                    Id = id,
                    Name = id,
                    ReleasedVersion = Version,
                    TargetPartition = id,
                }).ToArray(),
                null),
            "HEAD", source);

    private static ConfiguredPackageSource Failing(string source, string message) =>
        new(new StubSource(null, new InvalidOperationException(message)), "HEAD", source);

    /// <summary>A package source that lists exactly what it is told to, or refuses to list at
    /// all — standing in for the registry's own catalog read.</summary>
    private sealed class StubSource(IReadOnlyList<PackageManifest>? packages, Exception? failure)
        : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            failure is null
                ? Observable.Return(packages ?? [])
                : Observable.Throw<IReadOnlyList<PackageManifest>>(failure);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef) =>
            Observable.Return<IReadOnlyList<PackageFile>>([]);
    }
}
