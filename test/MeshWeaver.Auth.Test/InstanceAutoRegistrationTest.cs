using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Pins first-startup instance auto-registration end to end at the service layer (the flow behind
/// <c>POST /api/instances/register</c>): a platform admin mints a bootstrap key (<c>mwr_…</c>), a
/// new deployment presents it with a desired id, and the registry creates the instance OWNED BY THE
/// MINTER — default grants seeded, usage stamped, the issued <c>mwi_</c> key honoured by the
/// registry authenticator. And the gate fails closed: an unknown, revoked or expired key registers
/// nothing.
/// </summary>
public class InstanceAutoRegistrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // Registers RegistrationKeyService (and the rest of the catalog wiring) on the mesh —
            // on a portal this comes in with the plugin catalog itself.
            .AddPluginCatalog();

    private RegistrationKeyService Keys() =>
        Mesh.ServiceProvider.GetRequiredService<RegistrationKeyService>();

    private MeshWeaverInstanceService Service(params string[] defaultGrants)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(defaultGrants.Select((entry, i) => new KeyValuePair<string, string?>(
                $"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:{i}", entry)))
            .Build();
        return new MeshWeaverInstanceService(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
            configuration);
    }

    private IObservable<MeshNode?> ReadAsSystem(string path)
    {
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => Mesh.GetMeshNode(path, TimeSpan.FromSeconds(10)))
            .Take(1);
    }

    [Fact]
    public async Task BootstrapKey_RegistersInstance_OwnedByMinter_WithDefaultsAndWorkingKey()
    {
        var mint = await Keys()
            .Mint("key-admin", "Key Admin", "keyadmin@test.com", "env scaffold")
            .Should().Emit();
        Assert.StartsWith("mwr_", mint.RawKey);

        var registration = await Service("Plugins/*")
            .RegisterWithBootstrapKey(mint.RawKey, "auto-instance-a", "Auto A")
            .Should().Emit();

        // Owned by the admin who MINTED the bootstrap key — in their partition, like a hand
        // registration by them.
        registration.Instance.OwnerUserId.Should().Be("key-admin");
        registration.Node.Path.Should().Be("key-admin/MeshWeaverInstance/auto-instance-a");
        Assert.StartsWith("mwi_", registration.RawKey);

        // The DefaultGrants seed applies to the auto-registered instance too.
        var grantNode = await ReadAsSystem(
            MeshWeaverInstanceNodeType.GrantPath("auto-instance-a")).Should().Emit();
        grantNode.Should().NotBeNull();
        var grant = grantNode!.ContentAs<PluginGrant>(Mesh.JsonSerializerOptions)!;
        grant.Allows("Plugins", "Store").Should().BeTrue();

        // The issued instance key authenticates against the registry surface immediately.
        var authenticator = new InstanceRegistryAuthenticator(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>());
        var caller = await authenticator
            .Authenticate($"Bearer {registration.RawKey}")
            .Should().Emit();
        caller.Should().NotBeNull();
        caller!.Allows("Plugins", "Agent").Should().BeTrue();

        // The use is stamped onto the key record — the audit half of unattended registration.
        var keyNode = await ReadAsSystem(mint.Node.Path).Should().Emit();
        var key = keyNode!.ContentAs<RegistrationKey>(Mesh.JsonSerializerOptions)!;
        key.UsageCount.Should().Be(1);
        key.LastUsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UnknownKey_FailsClosed()
    {
        await Assert.ThrowsAsync<InvalidBootstrapKeyException>(() => Service()
            .RegisterWithBootstrapKey(RegistrationKeys.Generate(), "auto-instance-unknown")
            .FirstAsync().ToTask(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RevokedKey_FailsClosed()
    {
        var mint = await Keys()
            .Mint("key-admin", "Key Admin", "keyadmin@test.com", "to be revoked")
            .Should().Emit();
        await Keys().SetRevoked(mint.Node.Path, revoked: true).Should().Emit();

        await Assert.ThrowsAsync<InvalidBootstrapKeyException>(() => Service()
            .RegisterWithBootstrapKey(mint.RawKey, "auto-instance-revoked")
            .FirstAsync().ToTask(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredKey_FailsClosed()
    {
        var mint = await Keys()
            .Mint("key-admin", "Key Admin", "keyadmin@test.com", "already expired",
                expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1))
            .Should().Emit();

        await Assert.ThrowsAsync<InvalidBootstrapKeyException>(() => Service()
            .RegisterWithBootstrapKey(mint.RawKey, "auto-instance-expired")
            .FirstAsync().ToTask(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TakenId_IsATypedConflict()
    {
        var mint = await Keys()
            .Mint("key-admin", "Key Admin", "keyadmin@test.com", "conflict case")
            .Should().Emit();

        await Service().RegisterWithBootstrapKey(mint.RawKey, "auto-instance-dup").Should().Emit();
        await Assert.ThrowsAsync<InstanceIdTakenException>(() => Service()
            .RegisterWithBootstrapKey(mint.RawKey, "auto-instance-dup")
            .FirstAsync().ToTask(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoredCredential_IsResolvedForCatalogCalls_AndExplicitTokenWins()
    {
        // The consumer half: after auto-registration stored a credential, catalog calls resolve it;
        // an explicitly configured token always takes precedence.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        const string url = "https://registry.example.test";
        var node = new MeshNode(
            PluginRegistryCredentials.Path(url).Split('/').Last(), PluginRegistryCredentials.Namespace)
        {
            Name = "Registry credential (test)",
            NodeType = PluginRegistryCredentials.NodeType,
            State = MeshNodeState.Active,
            Content = new PluginRegistryCredential
            {
                RegistryUrl = url,
                InstanceId = "auto-instance-cred",
                ProtectedKey = "mwi_stored-key",
                RegisteredAt = DateTimeOffset.UtcNow,
            },
        };
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(node).Should().Emit();

        var resolver = new RegistryTokenResolver(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<RegistryTokenResolver>>());

        var stored = await resolver
            .ResolveToken(new PluginRegistryReference { Url = url })
            .Should().Emit();
        stored.Should().Be("mwi_stored-key");

        var explicitWins = await resolver
            .ResolveToken(new PluginRegistryReference { Url = url, Token = "mwi_configured" })
            .Should().Emit();
        explicitWins.Should().Be("mwi_configured");

        var absent = await resolver
            .ResolveToken(new PluginRegistryReference { Url = "https://other.example.test" })
            .Should().Emit();
        absent.Should().Be("");
    }
}
