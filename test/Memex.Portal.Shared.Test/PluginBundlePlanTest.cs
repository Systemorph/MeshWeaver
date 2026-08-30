using System.Net;
using System.Reactive.Linq;
using System.Text.Json;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
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
/// 🚨 <b>AN INSTANCE ON THE WRONG PLAN CANNOT PULL A PACKAGE ABOVE IT.</b>
///
/// <para>The Store sells plans and every package declares the plan it belongs to; the instance's
/// record carries its plan (#2804) and the registry serves only the packages that plan covers,
/// from the sources the instance is granted. This fixture pins that boundary where it is enforced — on the registry's bundle routes, over a real
/// HTTP pipeline, with the production <see cref="InstanceRegistryAuthenticator"/> resolving the
/// caller's grant AND the plan ladder off this mesh's <c>Admin/Tiers</c> nodes. Nothing is mocked:
/// the instances are registered through <see cref="MeshWeaverInstanceService.Register"/> on a plan
/// with a plan-less whole-source default grant, the packages are installed by <see cref="PackageInstaller"/> with
/// their declared tier, and the tier nodes are written the way the Store seeds them.</para>
///
/// <para>Both directions, as always: the free-plan instance still gets the free package (a gate
/// that refuses everyone is not a licence), the pro-plan instance gets both, and on a registry with
/// NO tier nodes a paid plan reads as the baseline — free flows, nothing above it does; a ladder
/// that cannot be read must never widen a licence.</para>
/// </summary>
public class PluginBundlePlanTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string FreeApp = "PlanFreeApp";
    private const string ProApp = "PlanProApp";
    private const string AbsentApp = "PlanNeverInstalled";
    private const string Source = "Plugins";
    private const string Version = "2.0.0";
    private const string FreeInstance = "plan-consumer-free";
    private const string ProInstance = "plan-consumer-pro";
    private const string TierNodeType = "Store/Tier";

    /// <summary>The tier node's content as the registry reads it — the rank and the all-access
    /// flag, the two fields of the Store's <c>TierContent</c> that <see cref="PlanTierLadder"/>
    /// consumes. A test-local type because the Store is a module this fixture does not run; the
    /// ladder reads the SERIALIZED content shape-tolerantly, never the CLR type, which is exactly
    /// what lets the platform read a node whose type is Store source.</summary>
    public record TierNode
    {
        public int Rank { get; init; }
        public bool AllAccess { get; init; }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            // The Store/Tier node type, as far as this fixture needs it: enough for Admin/Tiers/*
            // nodes to be creatable on a mesh that does not run the Store.
            .AddMeshNodes(new MeshNode(TierNodeType)
            {
                Name = "Tier",
                IsSatelliteType = false,
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<TierNode>()),
            })
            .ConfigureHub(config => config.WithType<TierNode>(nameof(TierNode)));

    // ── the real registration + grant path, one plan-scoped default entry per instance ────────

    private MeshWeaverInstanceService InstanceService(params string[] defaultGrants) =>
        new(Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(defaultGrants.Select((entry, i) =>
                    new KeyValuePair<string, string?>(
                        $"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:{i}", entry)))
                .Build());

    /// <summary>Registers an instance ON <paramref name="tier"/> (null = the baseline, exactly what
    /// open registration yields) with plan-less default grants — the plan lives on the record
    /// (#2804), the grant says only which sources.</summary>
    private Task<string> RegisterInstance(string instanceId, string? tier, params string[] defaultGrants) =>
        InstanceService(defaultGrants)
            .Register("plan-owner", "Plan Owner", "owner@test.com", instanceId, instanceId, tier: tier)
            .Select(r => r.RawKey)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .Await();

    /// <summary>Where <see cref="MeshWeaverInstanceService.Register"/> puts the record: the owner's
    /// partition.</summary>
    private static string InstancePath(string instanceId) =>
        $"plan-owner/{MeshWeaverInstanceService.InstanceNamespace}/{instanceId}";

    /// <summary>Installs a package the production way, carrying the plan it declares — the
    /// <c>content.tier</c> a node-repo source reads off the package root.</summary>
    private Task<InstallResult> InstallPackage(string id, string tier) =>
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
                    Source = Source,
                    Tier = tier,
                },
                [new PackageFile($"{id}/Doc.md", $"# {id}")],
                "HEAD")
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(120))
            .Await();

    /// <summary>The plan ladder as the Store seeds it — one <c>Admin/Tiers/{id}</c> node per plan,
    /// its rank on the content. Written as System: the Admin partition is exactly what a registering
    /// instance's owner cannot reach.</summary>
    private Task SeedLadder()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        return new[] { ("free", 0), ("pro", 20) }
            .Select(plan => Observable.Defer(() =>
            {
                var system = access.ImpersonateAsSystem();
                return mesh.CreateOrUpdateNode(new MeshNode(plan.Item1, PlanTierLadder.Namespace)
                    {
                        Name = plan.Item1,
                        NodeType = TierNodeType,
                        State = MeshNodeState.Active,
                        Content = new TierNode { Rank = plan.Item2 },
                    })
                    .Finally(() => system.Dispose());
            }))
            .Concat()
            .LastAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .Await();
    }

    // ── the routes, over a real HTTP pipeline ─────────────────────────────────────────────────

    private async Task<WebApplication> StartBundleHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        // The mesh's OWN authenticator, not a private one: a promotion invalidates the verdict
        // cached on the mesh-scoped singleton, which is the one production endpoints resolve.
        builder.Services.AddSingleton(Mesh.ServiceProvider.GetRequiredService<InstanceRegistryAuthenticator>());
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

    private static string BundleRoute(string plugin) => $"{PluginBundleEndpoints.RoutePrefix}/{plugin}/{Version}";
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
    /// 🚨 The whole boundary in one test: the free-plan instance gets the free package, is not
    /// even shown the pro one, and its fetch of it is byte-identical to fetching a package that
    /// does not exist — while the pro-plan instance, holding the SAME source, gets both.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnInstanceOnTheFreePlan_CannotPullAProPackage()
    {
        await SeedLadder();
        await InstallPackage(FreeApp, "free");
        await InstallPackage(ProApp, "pro");
        var freeKey = await RegisterInstance(FreeInstance, "free", $"{Source}/*");
        var proKey = await RegisterInstance(ProInstance, "pro", $"{Source}/*");

        var app = await StartBundleHost();
        await using var _ = app;

        // The free plan: its own tier is served, the tier above is not — and is not listed.
        using var freeIndex = await Get(app, IndexRoute, freeKey);
        freeIndex.StatusCode.Should().Be(HttpStatusCode.OK);
        var freeSees = await IndexedPlugins(freeIndex);
        freeSees.Should().Contain(FreeApp, "the free plan covers a package declaring `free`");
        freeSees.Should().NotContain(ProApp,
            "a free-plan instance must not even learn that a pro package is installed here");

        using var servedFree = await Get(app, BundleRoute(FreeApp), freeKey);
        servedFree.StatusCode.Should().Be(HttpStatusCode.OK, "a gate that refuses everyone is not a licence");

        using var refusedPro = await Get(app, BundleRoute(ProApp), freeKey);
        using var absent = await Get(app, BundleRoute(AbsentApp), freeKey);
        refusedPro.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a package above the instance's plan is not deployable to it");
        (await refusedPro.Content.ReadAsByteArrayAsync())
            .Should().Equal(await absent.Content.ReadAsByteArrayAsync(),
                "the refusal must be indistinguishable from not-found — a reason string is an oracle");

        // The pro plan, same source, same registry: both.
        using var proIndex = await Get(app, IndexRoute, proKey);
        (await IndexedPlugins(proIndex)).Should().Contain([FreeApp, ProApp],
            "the pro plan covers everything ranked at or below it");
        using var servedPro = await Get(app, BundleRoute(ProApp), proKey);
        servedPro.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// 🚨 No ladder, no widening. A registry without tier nodes cannot rank a paid plan, so an
    /// instance on `pro` reads as the BASELINE there: the free package flows (a local self-registry
    /// serves its free packages ladder or not), the pro one does not — pinned so a missing Store
    /// seed can never read as "every plan is all-access".
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task WithoutTierNodes_APaidPlanReadsAsTheBaseline()
    {
        await InstallPackage(FreeApp, "free");
        await InstallPackage(ProApp, "pro");
        var proKey = await RegisterInstance(ProInstance, "pro", $"{Source}/*");

        var app = await StartBundleHost();
        await using var _ = app;

        // No ladder: "pro" cannot be ranked, so the pro package is not covered even for an instance
        // that names that plan — while the baseline still flows, because "free" ranks at the
        // baseline by definition (a local self-registry serves its free packages ladder or not).
        using var index = await Get(app, IndexRoute, proKey);
        index.StatusCode.Should().Be(HttpStatusCode.OK, "the caller authenticates");
        var sees = await IndexedPlugins(index);
        sees.Should().Contain(FreeApp, "the baseline needs no ladder");
        sees.Should().NotContain(ProApp, "a tier the registry cannot rank is covered by nothing");
        using var refused = await Get(app, BundleRoute(ProApp), proKey);
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// 🚨 THE HOLE #2804 CLOSES. Every instance registered before the plan lane carries plan-less
    /// grant entries (memex-cloud: <c>Plugins/*</c>), and "a plan-less entry covers every tier"
    /// let them pull pro and enterprise bundles. Now the licence is the INSTANCE's plan, and an
    /// instance whose record names none is on the baseline — free, not unlimited.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ALegacyPlanLessGrant_IsCappedByTheInstancePlan()
    {
        await SeedLadder();
        await InstallPackage(FreeApp, "free");
        await InstallPackage(ProApp, "pro");
        var legacyKey = await RegisterInstance(FreeInstance, null, $"{Source}/*");

        var app = await StartBundleHost();
        await using var _ = app;

        using var index = await Get(app, IndexRoute, legacyKey);
        var sees = await IndexedPlugins(index);
        sees.Should().Contain(FreeApp, "the baseline plan covers the free package");
        sees.Should().NotContain(ProApp, "a plan-less grant on an instance with no plan is a FREE licence, not an unlimited one");
        using var refused = await Get(app, BundleRoute(ProApp), legacyKey);
        using var absent = await Get(app, BundleRoute(AbsentApp), legacyKey);
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await refused.Content.ReadAsByteArrayAsync())
            .Should().Equal(await absent.Content.ReadAsByteArrayAsync(), "indistinguishable from not-found");
    }

    /// <summary>
    /// 🚨 PROMOTION IS ONE FIELD, AND IT TAKES EFFECT AT ONCE. A global admin sets the plan on the
    /// instance record; the same process forgets its cached verdict, so the very next request is
    /// decided with the new plan — no grant string edited, no cache window waited out.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task PromotingTheInstance_WidensTheNextRequest()
    {
        await SeedLadder();
        await InstallPackage(FreeApp, "free");
        await InstallPackage(ProApp, "pro");
        var key = await RegisterInstance(FreeInstance, "free", $"{Source}/*");

        var app = await StartBundleHost();
        await using var _ = app;

        using var before = await Get(app, BundleRoute(ProApp), key);
        before.StatusCode.Should().Be(HttpStatusCode.NotFound, "on the free plan the pro package is not deployable");

        // The promotion: the plan service the admin tab calls, against the record's own path. The
        // bundle host above resolves through the mesh's registered authenticator, whose cache the
        // promotion invalidates — so the next request already sees `pro`.
        var plans = Mesh.ServiceProvider.GetRequiredService<InstancePlanService>();
        await plans.SetPlan(InstancePath(FreeInstance), "pro")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await();

        using var after = await Get(app, BundleRoute(ProApp), key);
        after.StatusCode.Should().Be(HttpStatusCode.OK, "the promoted instance pulls the pro package on its next request");
        using var index = await Get(app, IndexRoute, key);
        (await IndexedPlugins(index)).Should().Contain([FreeApp, ProApp]);
    }
}
