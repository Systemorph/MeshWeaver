using System.Net;
using System.Net.Http.Json;
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
/// 🎟️ <b>COUPONS — a deployment registers on its plan from the beginning.</b> A coupon is a
/// registration key with a plan, an expiry and a usage limit (<see cref="RegistrationKey.MaxUsages"/>),
/// minted for ONE client deployment through <c>POST /api/instances/coupons</c> — a credential scoped
/// to that act alone, never <c>Bootstrap:Secret</c>. Pinned over the real endpoints: a single-use
/// coupon registers exactly one instance, on its plan, and is then refused like an expired key; the
/// mint is absent until configured, refuses a wrong credential, and refuses a plan the registry's
/// ladder does not know before anything is written.
/// </summary>
public class RegistrationCouponTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TierNodeType = "Store/Tier";
    private const string MintSecret = "test-coupon-mint-credential-0123456789abcdef";
    private const string Owner = "coupon-owner";

    /// <summary>The Store's tier content shape, test-local (this fixture does not run the Store).</summary>
    public record TierNode
    {
        public int Rank { get; init; }
        public bool AllAccess { get; init; }
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .AddMeshNodes(new MeshNode(TierNodeType)
            {
                Name = "Tier",
                IsSatelliteType = false,
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<TierNode>()),
            })
            .ConfigureHub(config => config.WithType<TierNode>(nameof(TierNode)));

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    private Task SeedLadder(CancellationToken ct) =>
        new[] { ("free", 0, false), ("pro", 20, false), ("dedicated", 25, true) }
            .Select(t => Access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
                .CreateOrUpdateNode(new MeshNode(t.Item1, PlanTierLadder.Namespace)
                {
                    Name = t.Item1,
                    NodeType = TierNodeType,
                    State = MeshNodeState.Active,
                    Content = new TierNode { Rank = t.Item2, AllAccess = t.Item3 },
                })))
            .Concat()
            .LastAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .Await(ct);

    private MeshWeaverInstanceService InstanceService() => new(
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh,
        Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PluginCatalog:Sources:0:Name"] = "Plugins",
            ["PluginCatalog:Sources:0:RepoPath"] = "/plugin-repos/plugins",
        }).Build());

    private async Task<WebApplication> StartRegistry(bool configureMint, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (configureMint)
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RegistrationCouponPayloads.SecretConfigKey] = MintSecret,
                [RegistrationCouponPayloads.OwnerConfigKey] = Owner,
            });
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(InstanceService());
        var app = builder.Build();
        app.MapInstanceRegistration();
        await app.StartAsync(ct);
        return app;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> MintCoupon(
        WebApplication app, string? secret, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RegistrationCouponPayloads.Route)
        {
            Content = JsonContent.Create(body),
        };
        if (secret is not null)
            request.Headers.TryAddWithoutValidation(RegistrationCouponPayloads.SecretHeader, secret);
        using var response = await app.GetTestClient().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (response.StatusCode, json);
    }

    private static Task<HttpResponseMessage> Register(WebApplication app, string coupon, string instanceId) =>
        app.GetTestClient().PostAsJsonAsync(
            InstanceRegistrationPayloads.Route,
            new InstanceRegistrationPayloads.Request(coupon, instanceId, DisplayName: instanceId),
            InstanceRegistrationPayloads.Json);

    // ── pure ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AUsageLimit_IsEnforcedLikeExpiry_AndNoLimitStaysUnlimited()
    {
        var now = DateTimeOffset.UtcNow;
        var coupon = new RegistrationKey { MaxUsages = 1 };
        coupon.IsUsable(now).Should().BeTrue("an unused single-use coupon registers");
        (coupon with { UsageCount = 1 }).IsUsable(now).Should().BeFalse("a spent coupon fails validation like an expired key");
        (coupon with { UsageCount = 1 }).IsUsedUp.Should().BeTrue();
        new RegistrationKey { UsageCount = 500 }.IsUsable(now).Should().BeTrue(
            "a key with no limit (every key minted before coupons existed) is unchanged");
    }

    [Fact]
    public void TheMintBounds_RefuseACouponWithoutPlanExpiryOrDescription()
    {
        RegistrationCouponPayloads.Refusal(new("dedicated", "fabrikam ramp-up", 14, 1)).Should().BeNull();
        RegistrationCouponPayloads.Refusal(new("dedicated", "fabrikam ramp-up", 14, null)).Should().BeNull("no limit stated ⇒ single use");
        RegistrationCouponPayloads.Refusal(new("", "fabrikam", 14, 1)).Should().Contain("tier is required");
        RegistrationCouponPayloads.Refusal(new("pro", " ", 14, 1)).Should().Contain("description is required");
        RegistrationCouponPayloads.Refusal(new("pro", "fabrikam", null, 1)).Should().Contain("expiresDays");
        RegistrationCouponPayloads.Refusal(new("pro", "fabrikam", 365, 1)).Should().Contain("expiresDays");
        RegistrationCouponPayloads.Refusal(new("pro", "fabrikam", 14, 0)).Should().Contain("maxUsages");
        RegistrationCouponPayloads.Refusal(new("pro", "fabrikam", 14, 1000)).Should().Contain("maxUsages");
    }

    // ── over the real endpoints ─────────────────────────────────────────────────────────────────

    [Fact(Timeout = 300_000)]
    public async Task ASingleUseCoupon_RegistersOneInstanceOnItsPlan_ThenIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var coupon = await Mesh.ServiceProvider.GetRequiredService<RegistrationKeyService>()
            .Mint(Owner, Owner, "", "fabrikam ramp-up", DateTimeOffset.UtcNow.AddDays(14), "dedicated", maxUsages: 1)
            .Select(r => r.RawKey).FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await(ct);
        var app = await StartRegistry(configureMint: false, ct);
        await using var _ = app;

        using (var first = await Register(app, coupon, "fabrikam"))
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            var issued = await first.Content.ReadFromJsonAsync<InstanceRegistrationPayloads.Response>(
                InstanceRegistrationPayloads.Json, ct);
            issued!.Plan.Should().Be("dedicated", "the instance enrols into the coupon's plan from the beginning");
        }

        using var second = await Register(app, coupon, "fabrikam-two");
        second.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a single-use coupon is spent by its one registration and then refused like an expired key");
    }

    [Fact(Timeout = 300_000)]
    public async Task TheMint_IsAbsentUntilConfigured()
    {
        var app = await StartRegistry(configureMint: false, TestContext.Current.CancellationToken);
        await using var _ = app;
        var (status, _) = await MintCoupon(app, MintSecret, new { tier = "pro", description = "fabrikam", expiresDays = 14 });
        status.Should().Be(HttpStatusCode.NotFound, "a registry that configures no mint credential has no mint");
    }

    [Fact(Timeout = 300_000)]
    public async Task TheMint_RefusesAWrongCredential_AndAnUnknownPlan()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLadder(ct);
        var app = await StartRegistry(configureMint: true, ct);
        await using var _ = app;

        var (wrong, _) = await MintCoupon(app, "not-the-credential", new { tier = "pro", description = "fabrikam", expiresDays = 14 });
        wrong.Should().Be(HttpStatusCode.Unauthorized);
        var (none, _) = await MintCoupon(app, null, new { tier = "pro", description = "fabrikam", expiresDays = 14 });
        none.Should().Be(HttpStatusCode.Unauthorized);

        var (unknown, body) = await MintCoupon(app, MintSecret, new { tier = "platinum", description = "fabrikam", expiresDays = 14 });
        unknown.Should().Be(HttpStatusCode.BadRequest, "a plan the ladder does not know licenses nothing — refused before anything is written");
        body.GetProperty("error").GetString().Should().Contain("not on this registry's tier ladder");
    }

    [Fact(Timeout = 300_000)]
    public async Task AMintedCoupon_CarriesItsPlan_AndRegistersOnIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedLadder(ct);
        var app = await StartRegistry(configureMint: true, ct);
        await using var _ = app;

        var (status, body) = await MintCoupon(app, MintSecret,
            new { tier = "Dedicated", description = "fabrikam ramp-up", expiresDays = 14, maxUsages = 1 });
        status.Should().Be(HttpStatusCode.OK);
        var coupon = body.GetProperty("coupon").GetString()!;
        coupon.Should().StartWith("mwr_", "a coupon is a registration key");
        body.GetProperty("tier").GetString().Should().Be("dedicated", "the plan is stored canonical");
        body.GetProperty("maxUsages").GetInt32().Should().Be(1);

        using var registered = await Register(app, coupon, "fabrikam-minted");
        registered.StatusCode.Should().Be(HttpStatusCode.OK);
        (await registered.Content.ReadFromJsonAsync<InstanceRegistrationPayloads.Response>(
            InstanceRegistrationPayloads.Json, ct))!.Plan.Should().Be("dedicated");
        using var again = await Register(app, coupon, "fabrikam-minted-two");
        again.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "single use");
    }
}

/// <summary>
/// 🚨 The mint reads the registry's tier ladder AS SYSTEM. Its HTTP caller is anonymous (the mint
/// credential is a header, not an identity), and on a registry with <c>Access:DenyAnonymous</c> a
/// ladder query under that identity answers an EMPTY Initial — with a cold cache every plan then read
/// "not on the ladder" (400) and that empty ladder was cached for a minute. This fixture states the
/// switch, so the mint below reaches a COLD ladder as the anonymous HTTP caller.
/// </summary>
public class RegistrationCouponOnAClosedRegistryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TierNodeType = "Store/Tier";
    private const string MintSecret = "test-coupon-mint-credential-closed-registry";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .AddMeshNodes(new MeshNode(TierNodeType)
            {
                Name = "Tier",
                IsSatelliteType = false,
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<RegistrationCouponTest.TierNode>()),
            })
            .ConfigureHub(config => config.WithType<RegistrationCouponTest.TierNode>(nameof(RegistrationCouponTest.TierNode)))
            .ConfigureServices(services => services.AddSingleton<IConfiguration>(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AnonymousAccess.DenyAnonymousConfigKey] = "true",
                }).Build()));

    [Fact(Timeout = 300_000)]
    public async Task TheMint_ReadsTheLadderAsSystem_SoAnAnonymousCallerOnAClosedRegistryStillMints()
    {
        var ct = TestContext.Current.CancellationToken;
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
                .CreateOrUpdateNode(new MeshNode("dedicated", PlanTierLadder.Namespace)
                {
                    Name = "dedicated",
                    NodeType = TierNodeType,
                    State = MeshNodeState.Active,
                    Content = new RegistrationCouponTest.TierNode { Rank = 25, AllAccess = true },
                }))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await(ct);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RegistrationCouponPayloads.SecretConfigKey] = MintSecret,
            [RegistrationCouponPayloads.OwnerConfigKey] = "coupon-owner",
        });
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        var app = builder.Build();
        app.MapInstanceRegistration();
        await app.StartAsync(ct);
        await using var _ = app;

        using var request = new HttpRequestMessage(HttpMethod.Post, RegistrationCouponPayloads.Route)
        {
            Content = JsonContent.Create(new { tier = "dedicated", description = "fabrikam ramp-up", expiresDays = 14 }),
        };
        request.Headers.TryAddWithoutValidation(RegistrationCouponPayloads.SecretHeader, MintSecret);
        // The fixture logs a DevLogin HOST identity in for every flow without one; the real registry
        // has none — its mint caller is anonymous. Clear it so the request runs exactly as anonymous.
        access.ClearHostIdentity();
        using var response = await app.GetTestClient().SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a plan the ladder knows is minted even though the HTTP caller is anonymous on a closed registry: " + text);
    }
}
