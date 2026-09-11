using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Text.Json;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
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
/// 🅿️ The registry's key-lifecycle surface over the REAL endpoint and the real authenticator
/// (MeshWeaver#2802): <c>/api/instances/self</c> and <c>/api/instances/self/key/{stage,commit,revoke}</c>.
///
/// <para>The defect these pin: a rotation used to ADOPT the new hash through whatever
/// <see cref="IInstanceKeyRegistry"/> the control instance's hub resolved — a store that does not hold
/// the instance — after Key Vault had already been written. The replacement is served by the
/// registry, authorised by the instance's own key, and TWO-PHASE: the old key authenticates until the
/// new one is presented back. Every raw key below is minted by the test itself and never printed.</para>
/// </summary>
public class InstanceKeyRotationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    private MeshWeaverInstanceService Service() => new(
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh,
        Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

    private async Task<WebApplication> StartRegistry(MeshWeaverInstanceService service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton(Mesh.ServiceProvider.GetRequiredService<InstanceRegistryAuthenticator>());
        var app = builder.Build();
        // Mapped through registration, as every host maps it: a host that registers instances must
        // never 404 the key lifecycle.
        app.MapInstanceRegistration();
        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Call(
        WebApplication app, HttpMethod method, string route, string rawKey, object? body = null)
    {
        using var request = new HttpRequestMessage(method, route);
        request.Headers.TryAddWithoutValidation("Authorization", InstanceKeys.AuthorizationHeader(rawKey));
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await app.GetTestClient().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (response.StatusCode, json);
    }

    private static Task<(HttpStatusCode Status, JsonElement Body)> Self(WebApplication app, string rawKey) =>
        Call(app, HttpMethod.Get, InstanceKeyPayloads.SelfRoute, rawKey);

    private static Task<(HttpStatusCode Status, JsonElement Body)> Stage(WebApplication app, string rawKey, string newHash) =>
        Call(app, HttpMethod.Post, InstanceKeyPayloads.StageRoute, rawKey, new { keyHash = newHash });

    private static Task<(HttpStatusCode Status, JsonElement Body)> Commit(WebApplication app, string rawKey) =>
        Call(app, HttpMethod.Post, InstanceKeyPayloads.CommitRoute, rawKey);

    private static Task<(HttpStatusCode Status, JsonElement Body)> Revoke(WebApplication app, string rawKey) =>
        Call(app, HttpMethod.Post, InstanceKeyPayloads.RevokeRoute, rawKey);

    private Task<InstanceRegistrationResult> Register(MeshWeaverInstanceService service, string id) =>
        service.Register("owner", "Owner", "owner@test.com", id, id).Timeout(TimeSpan.FromSeconds(60)).Await();

    /// <summary>
    /// 🚨 THE CONTROL-INSTANCE CASE. A portal whose store does not hold the instance answers 401 to
    /// every key-lifecycle call — definitively, before anything is changed — which is what the
    /// operator's first step (<c>hosting-kv-rotate</c>) meets BEFORE it mints. Exactly 401, never
    /// "not 200": a 404 would mean the route is unmapped, which proves nothing.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARegistryThatDoesNotHoldTheInstance_RefusesEveryKeyCall_WithA401()
    {
        var service = Service();
        await using var app = await StartRegistry(service);
        var stranger = InstanceKeys.Generate();          // a key issued by some OTHER registry
        var next = InstanceKeys.Hash(InstanceKeys.Generate());

        (await Self(app, stranger)).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await Stage(app, stranger, next)).Status.Should().Be(HttpStatusCode.Unauthorized,
            "nothing can be staged for an instance this store does not hold");
        (await Commit(app, stranger)).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await Revoke(app, stranger)).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The old per-hub adoption on a store that does not hold the instance fails NAMING the id — never
    /// the silent "adopted" a rotation would have believed.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AdoptionByIdOnAStoreWithoutTheInstance_FailsByName()
    {
        IInstanceKeyRegistry registry = Service();
        var act = () => registry.AdoptKeyHash("not-in-this-registry", InstanceKeys.Hash(InstanceKeys.Generate()))
            .Timeout(TestTimeouts.Convergence).Await();
        (await act.Should().ThrowAsync<InstanceNotRegisteredException>())
            .Which.InstanceId.Should().Be("not-in-this-registry");
    }

    /// <summary>
    /// The registry-hosting path: stage with the current key, prove the new key, commit with the new
    /// key. 🚨 The old key authenticates THROUGH the stage and stops only at the commit — the window
    /// in which every failure of the old design turned into a delayed 401 storm.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task StageProveCommit_TheOldKeyStopsOnlyAtTheCommit()
    {
        var service = Service();
        await using var app = await StartRegistry(service);
        var registered = await Register(service, "rotate-two-phase");
        var oldKey = registered.RawKey;
        var newKey = InstanceKeys.Generate();

        var before = await Self(app, oldKey);
        before.Status.Should().Be(HttpStatusCode.OK);
        before.Body.GetProperty("instanceId").GetString().Should().Be("rotate-two-phase",
            "the operator compares this field with the record's instance id — its NAME is the wire contract");
        before.Body.GetProperty("key").GetString().Should().Be(InstanceKeyPayloads.CurrentKey);
        before.Body.GetProperty("staged").GetBoolean().Should().BeFalse();

        var staged = await Stage(app, oldKey, InstanceKeys.Hash(newKey));
        staged.Status.Should().Be(HttpStatusCode.OK);
        staged.Body.GetProperty("staged").GetBoolean().Should().BeTrue();

        var proof = await Self(app, newKey);
        proof.Status.Should().Be(HttpStatusCode.OK, "the staged key authenticates before it is stored anywhere");
        proof.Body.GetProperty("key").GetString().Should().Be(InstanceKeyPayloads.StagedKey);
        (await Self(app, oldKey)).Status.Should().Be(HttpStatusCode.OK,
            "🚨 staging retires nothing — the key the pods present keeps authenticating");

        (await Stage(app, newKey, InstanceKeys.Hash(InstanceKeys.Generate()))).Status
            .Should().Be(HttpStatusCode.Conflict, "a staged key never stages another");
        (await Commit(app, oldKey)).Status.Should().Be(HttpStatusCode.Conflict,
            "the CURRENT key cannot commit: whoever presents it has not received the new one");
        (await Self(app, oldKey)).Status.Should().Be(HttpStatusCode.OK, "a refused commit retired nothing");

        var committed = await Commit(app, newKey);
        committed.Status.Should().Be(HttpStatusCode.OK);
        committed.Body.GetProperty("key").GetString().Should().Be(InstanceKeyPayloads.CurrentKey);
        committed.Body.GetProperty("staged").GetBoolean().Should().BeFalse();

        (await Self(app, oldKey)).Status.Should().Be(HttpStatusCode.Unauthorized, "the commit retires the previous key");
        (await Self(app, newKey)).Body.GetProperty("key").GetString().Should().Be(InstanceKeyPayloads.CurrentKey);
        (await Commit(app, newKey)).Status.Should().Be(HttpStatusCode.OK, "a repeated commit is idempotent");
    }

    /// <summary>A key found somewhere it should not be is revoked by presenting it — nobody reads its value.</summary>
    [Fact(Timeout = 120_000)]
    public async Task RevokingAPresentedKey_MakesItStopAuthenticating()
    {
        var service = Service();
        await using var app = await StartRegistry(service);
        var registered = await Register(service, "revoke-me");
        var key = registered.RawKey;
        (await Self(app, key)).Status.Should().Be(HttpStatusCode.OK, "CONTROL: the key authenticates before the revocation");

        var revoked = await Revoke(app, key);
        revoked.Status.Should().Be(HttpStatusCode.OK);
        revoked.Body.GetProperty("instanceId").GetString().Should().Be("revoke-me",
            "the revocation names the instance the key belonged to — how an operator learns whose it was");
        revoked.Body.GetProperty("key").GetString().Should().Be("revoked");

        (await Self(app, key)).Status.Should().Be(HttpStatusCode.Unauthorized, "a revoked key no longer authenticates");
        var authenticated = await Mesh.ServiceProvider.GetRequiredService<InstanceRegistryAuthenticator>()
            .Authenticate(InstanceKeys.AuthorizationHeader(key)).Timeout(TestTimeouts.Convergence).Await();
        authenticated.Should().BeNull("and every OTHER surface the registry serves refuses it too — one authenticator");
    }

    /// <summary>
    /// The registry-side admin act: revoke an instance BY ID, with no key presented — for a key whose
    /// value nobody should have to know. Every key of the instance stops authenticating; the test
    /// base signs in as the DevLogin platform admin, so the global-admin gate is satisfied.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RevokingByIdAsAGlobalAdmin_StopsEveryKeyOfTheInstance()
    {
        var service = Service();
        await using var app = await StartRegistry(service);
        var registered = await Register(service, "revoke-by-id");
        var current = registered.RawKey;
        var staged = InstanceKeys.Generate();
        (await Stage(app, current, InstanceKeys.Hash(staged))).Status.Should().Be(HttpStatusCode.OK);
        (await Self(app, staged)).Status.Should().Be(HttpStatusCode.OK, "CONTROL: both keys authenticate before the revocation");

        await ((IInstanceKeyRegistry)service).RevokeKey("revoke-by-id").Timeout(TimeSpan.FromSeconds(60)).Await();

        (await Self(app, current)).Status.Should().Be(HttpStatusCode.Unauthorized, "the current key is revoked");
        (await Self(app, staged)).Status.Should().Be(HttpStatusCode.Unauthorized, "and so is the staged one");

        var absent = () => ((IInstanceKeyRegistry)service).RevokeKey("never-registered")
            .Timeout(TestTimeouts.Convergence).Await();
        await absent.Should().ThrowAsync<InstanceNotRegisteredException>(
            "revoking an id this registry does not hold fails by name — never a silent no-op on the wrong store");
    }

    /// <summary>
    /// An immediate adoption supersedes a staged rotation: adopting the hash that is ALREADY current
    /// still retires the staged key (Copilot review on #4055 — the old short-cut returned first and
    /// left it authenticating), and adopting the STAGED hash promotes it without re-indexing it.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnImmediateAdoption_SupersedesAStagedRotation()
    {
        var service = Service();
        await using var app = await StartRegistry(service);

        var first = await Register(service, "adopt-current");
        var stagedA = InstanceKeys.Generate();
        (await Stage(app, first.RawKey, InstanceKeys.Hash(stagedA))).Status.Should().Be(HttpStatusCode.OK);
        await service.AdoptKeyHash(first.Node.Path!, InstanceKeys.Hash(first.RawKey)).Timeout(TimeSpan.FromSeconds(60)).Await();
        (await Self(app, stagedA)).Status.Should().Be(HttpStatusCode.Unauthorized,
            "adopting the already-current hash retires the staged key");
        (await Self(app, first.RawKey)).Status.Should().Be(HttpStatusCode.OK, "…and keeps the current one");

        var second = await Register(service, "adopt-staged");
        var stagedB = InstanceKeys.Generate();
        (await Stage(app, second.RawKey, InstanceKeys.Hash(stagedB))).Status.Should().Be(HttpStatusCode.OK);
        await service.AdoptKeyHash(second.Node.Path!, InstanceKeys.Hash(stagedB)).Timeout(TimeSpan.FromSeconds(60)).Await();
        (await Self(app, stagedB)).Body.GetProperty("key").GetString().Should().Be(InstanceKeyPayloads.CurrentKey,
            "adopting the staged hash promotes it (its index entry already existed)");
        (await Self(app, second.RawKey)).Status.Should().Be(HttpStatusCode.Unauthorized, "…and retires the previous key");
    }

    /// <summary>
    /// A sync token exchanged with the STAGED key is bound to that key, so it survives the commit that
    /// promotes it (Copilot review on #4055: it used to be bound to the CURRENT key's hash and would
    /// have stopped resolving the moment the commit retired that key).
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ATokenExchangedWithTheStagedKey_SurvivesTheCommit()
    {
        var service = Service();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton(Mesh.ServiceProvider.GetRequiredService<InstanceRegistryAuthenticator>());
        await using var app = builder.Build();
        app.MapInstanceRegistration();
        app.MapInstanceTokenExchange();
        await app.StartAsync();

        var registered = await Register(service, "token-through-commit");
        var staged = InstanceKeys.Generate();
        (await Stage(app, registered.RawKey, InstanceKeys.Hash(staged))).Status.Should().Be(HttpStatusCode.OK);

        using var exchange = new HttpRequestMessage(HttpMethod.Post, SyncTokenPayloads.Route)
        {
            Content = JsonContent.Create(new SyncTokenPayloads.Request(), options: SyncTokenPayloads.Json),
        };
        exchange.Headers.TryAddWithoutValidation("Authorization", InstanceKeys.AuthorizationHeader(staged));
        using var exchanged = await app.GetTestClient().SendAsync(exchange);
        exchanged.StatusCode.Should().Be(HttpStatusCode.OK, "the staged key may exchange for a token");
        var token = (await exchanged.Content.ReadFromJsonAsync<SyncTokenPayloads.Response>(SyncTokenPayloads.Json))!.AccessToken;

        (await Commit(app, staged)).Status.Should().Be(HttpStatusCode.OK);

        var authenticator = Mesh.ServiceProvider.GetRequiredService<InstanceRegistryAuthenticator>();
        var outcome = await authenticator.AuthenticateOutcome($"{SyncAccessToken.Scheme} {token}")
            .Timeout(TestTimeouts.Convergence).Await();
        outcome.Instance.Should().NotBeNull("a token minted with the staged key still resolves once that key is current");
        outcome.Instance!.Instance.InstanceId.Should().Be("token-through-commit");
    }

    /// <summary>A short-lived token may read the catalog; it can never re-key or revoke.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ANonInstanceKeyCredential_IsRefusedBeforeAnythingIsResolved()
    {
        var service = Service();
        await using var app = await StartRegistry(service);
        using var request = new HttpRequestMessage(HttpMethod.Post, InstanceKeyPayloads.RevokeRoute);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer mwa_not-an-instance-key");
        using var response = await app.GetTestClient().SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
