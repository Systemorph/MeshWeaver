using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
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
/// 🚨 <b>OPEN REGISTRATION — the Homebrew default lands on the FREE plan, and nowhere else.</b>
///
/// <para>A local install (<c>memex-local registry https://memex.meshweaver.cloud</c>, no key)
/// presents NO bootstrap key. The registry accepts that only when its operator minted a registration
/// key for the plan un-keyed callers enrol into and configured it as
/// <c>PluginCatalog:OpenRegistration:Key</c>; the registration then runs exactly as if the caller
/// had presented that key — owned by its minting admin, seeded <c>&lt;source&gt;/*@&lt;plan&gt;</c>.
/// Moving the instance to a higher plan is an admin's edit of its grant on the registry, never
/// something the instance can ask for.</para>
///
/// <para>Two properties, both pinned over the real endpoint: with no open key configured an
/// un-keyed registration is refused with 401 exactly as before (a registry that configures nothing
/// stays closed), and with one configured the instance lands on THAT plan — a free-tier key must
/// never seed a plan-less <c>Plugins/*</c>.</para>
/// </summary>
public class InstanceOpenRegistrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Source = "Plugins";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    /// <summary>The registry's instance service with THIS configuration — one source, and the open
    /// key when the test supplies one. Registered on the host's request services, where the
    /// endpoint looks first.</summary>
    private MeshWeaverInstanceService InstanceService(string? openKey)
    {
        var config = new Dictionary<string, string?>
        {
            ["PluginCatalog:Sources:0:Name"] = Source,
            ["PluginCatalog:Sources:0:RepoPath"] = "/plugin-repos/plugins",
        };
        if (openKey is not null)
            config[MeshWeaverInstanceService.OpenRegistrationKeyConfigKey] = openKey;
        return new MeshWeaverInstanceService(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
            new ConfigurationBuilder().AddInMemoryCollection(config).Build());
    }

    /// <summary>Mints the key the operator would: for the FREE plan, owned by a platform admin.</summary>
    private Task<string> MintOpenKey() =>
        Mesh.ServiceProvider.GetRequiredService<RegistrationKeyService>()
            .Mint("open-owner", "Open Owner", "open@test.com", "open registration (free)", null, "free")
            .Select(r => r.RawKey)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(60))
            .Await();

    private async Task<WebApplication> StartRegistrationHost(string? openKey)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(InstanceService(openKey));
        var app = builder.Build();
        app.MapInstanceRegistration();
        await app.StartAsync();
        return app;
    }

    private static Task<HttpResponseMessage> RegisterOpenly(WebApplication app, string instanceId) =>
        app.GetTestClient().PostAsJsonAsync(
            InstanceRegistrationPayloads.Route,
            new InstanceRegistrationPayloads.Request("", instanceId, DisplayName: "A Mac"),
            InstanceRegistrationPayloads.Json);

    /// <summary>The grant node the registry wrote for an instance, read as System — the Admin
    /// partition is deliberately out of everyone else's reach.</summary>
    private Task<PluginGrant?> GrantOf(string instanceId)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => access.ImpersonateAsSystem(),
                _ => Mesh.GetMeshNode(MeshWeaverInstanceNodeType.GrantPath(instanceId), TimeSpan.FromSeconds(10)))
            .Take(1)
            .Select(node => node?.ContentAs<PluginGrant>(Mesh.JsonSerializerOptions))
            .Timeout(TimeSpan.FromSeconds(30))
            .Await();
    }

    [Fact(Timeout = 300_000)]
    public async Task WithoutAnOpenKey_AnUnkeyedRegistrationIsRefused()
    {
        var app = await StartRegistrationHost(openKey: null);
        await using var _ = app;

        using var response = await RegisterOpenly(app, "homebrew-mac-closed");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a registry that configures no open key stays closed — exactly the 401 an invalid key gets");
        (await GrantOf("homebrew-mac-closed")).Should().BeNull("nothing may be written for a refused registration");
    }

    [Fact(Timeout = 300_000)]
    public async Task WithAnOpenKeyForTheFreePlan_AnUnkeyedRegistrationLandsOnFree()
    {
        var openKey = await MintOpenKey();
        var app = await StartRegistrationHost(openKey);
        await using var _ = app;

        using var response = await RegisterOpenly(app, "homebrew-mac-open");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var issued = await response.Content.ReadFromJsonAsync<InstanceRegistrationPayloads.Response>(
            InstanceRegistrationPayloads.Json);
        issued!.InstanceKey.Should().StartWith("mwi_", "the instance is issued its own durable key, once");

        var grant = await GrantOf("homebrew-mac-open");
        grant.Should().NotBeNull("open registration seeds the plan's grant");
        grant!.Entries.Should().ContainSingle(e => e.ToString() == $"{Source}/*@free",
            "the instance enrols into the plan the open key was minted for — and ONLY that plan");
        grant.Entries.Should().NotContain(e => !e.IsPlanScoped,
            "a free-tier key must never seed a plan-less whole-source entry");
    }

    [Fact(Timeout = 300_000)]
    public async Task AnOpenKeyMintedWithoutAPlan_StillNeverGrantsMoreThanTheDefaults()
    {
        // A registry operator who mints the open key WITHOUT a plan gets today's behaviour for
        // un-keyed callers: the DefaultGrants seed (none here) and nothing else. Open registration
        // never invents entitlement.
        var openKey = await Mesh.ServiceProvider.GetRequiredService<RegistrationKeyService>()
            .Mint("open-owner", "Open Owner", "open@test.com", "open registration (no plan)")
            .Select(r => r.RawKey).FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await();
        var app = await StartRegistrationHost(openKey);
        await using var _ = app;

        using var response = await RegisterOpenly(app, "homebrew-mac-noplan");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GrantOf("homebrew-mac-noplan")).Should().BeNull(
            "no plan and no DefaultGrants → no grant node at all: registering is identity, not entitlement");
    }
}
