using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
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
/// Pins the short-lived access token END TO END against a real mesh: an instance exchanges its
/// durable <c>mwi_</c> key for a scoped <c>mwa_</c> token, and the registry authenticator resolves
/// it, narrows by its scope, and still consults the live sync licence.
///
/// <para>The unit tests in <c>SyncAccessTokenTest</c> prove the token's cryptography. What can only
/// be proved here is the wiring: that a token resolves to the right instance through the same index
/// the key uses, that it can only ever NARROW, and that a revoked or expired licence beats a
/// perfectly valid token.</para>
/// </summary>
public class SyncTokenAuthenticationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SigningKey = "test-signing-key-that-is-long-enough-32+";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services.AddSingleton(
                new PluginCatalogOptions { TokenSigningKey = SigningKey }));

    private MeshWeaverInstanceService Instances() => new(
        Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
        Mesh,
        Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
        new ConfigurationBuilder().Build());

    private InstanceRegistryAuthenticator Authenticator() => new(
        Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<InstanceRegistryAuthenticator>>());

    private SyncLicenseService Licenses() =>
        Mesh.ServiceProvider.GetRequiredService<SyncLicenseService>();

    /// <summary>Registers an instance and licences it for the given entries.</summary>
    private async Task<(string InstanceId, string RawKey, string KeyHash)> LicensedInstance(
        string instanceId, params (string Source, string Package)[] licensed)
    {
        var registration = await Instances()
            .Register("owner", "Owner", "owner@test.com", instanceId, instanceId)
            .Should().Emit();

        foreach (var (source, package) in licensed)
            await Licenses().Issue(new SyncLicenseRequest
            {
                InstanceId = instanceId,
                Source = source,
                PackageId = package,
                IssuedByUserId = "platform-admin",
                IssuedVia = "test",
            }).Should().Emit();

        return (instanceId, registration.RawKey, InstanceKeys.Hash(registration.RawKey));
    }

    private string MintToken(string instanceId, string keyHash, params string[] scope) =>
        SyncAccessToken.Mint(instanceId, keyHash, scope, DateTimeOffset.UtcNow,
            SyncAccessToken.DefaultLifetime, System.Text.Encoding.UTF8.GetBytes(SigningKey));

    [Fact]
    public async Task AToken_AuthenticatesAndResolvesToItsInstance()
    {
        var (id, _, hash) = await LicensedInstance("tok-a", ("Plugins", "*"));

        var caller = await Authenticator()
            .Authenticate($"Bearer {MintToken(id, hash)}")
            .Should().Emit();

        caller.Should().NotBeNull();
        caller!.Instance.InstanceId.Should().Be(id);
        caller.TokenScope.Should().NotBeNull("the caller came in on a token, not the durable key");
        caller.Allows("Plugins", "Publish").Should().BeTrue();
    }

    [Fact]
    public async Task AScopedToken_NarrowsBelowTheLicence()
    {
        // Licensed for the whole source, but the token asked for one package — the token wins
        // DOWNWARD. This is the CI case: a build agent holds a credential for exactly what it needs.
        var (id, _, hash) = await LicensedInstance("tok-narrow", ("Plugins", "*"));

        var caller = await Authenticator()
            .Authenticate($"Bearer {MintToken(id, hash, "Plugins/Publish")}")
            .Should().Emit();

        caller.Should().NotBeNull();
        caller!.Allows("Plugins", "Publish").Should().BeTrue();
        caller.Allows("Plugins", "Store").Should().BeFalse("the token narrowed to Publish");
    }

    [Fact]
    public async Task ATokenCanNeverWiden_TheLicenceStillDecides()
    {
        // A token minted for more than the licence covers grants nothing extra. The token carries
        // scope, never authority — so forging a broader scope (or holding a stale one after a
        // licence shrank) buys nothing.
        var (id, _, hash) = await LicensedInstance("tok-widen", ("Plugins", "Publish"));

        var caller = await Authenticator()
            .Authenticate($"Bearer {MintToken(id, hash, "Plugins/*", "Education/DataModeling")}")
            .Should().Emit();

        caller.Should().NotBeNull();
        caller!.Allows("Plugins", "Publish").Should().BeTrue();
        caller.Allows("Plugins", "Store").Should().BeFalse();
        caller.Allows("Education", "DataModeling").Should().BeFalse();
    }

    [Fact]
    public async Task RevokingTheLicence_TakesEffectImmediately_NotWhenTheTokenExpires()
    {
        // The reason authority is re-read on every request rather than baked into the token.
        var (id, _, hash) = await LicensedInstance("tok-revoke", ("Plugins", "Publish"));
        var token = MintToken(id, hash, "Plugins/Publish");

        var before = await Authenticator().Authenticate($"Bearer {token}").Should().Emit();
        before!.Allows("Plugins", "Publish").Should().BeTrue();

        await Licenses().RevokeAll(id, "platform-admin").Should().Emit();

        // The authenticator caches a resolution briefly; a fresh instance reads the live grant.
        var after = await Authenticator().Authenticate($"Bearer {token}").Should().Emit();
        after.Should().NotBeNull("the token is still cryptographically valid");
        after!.Allows("Plugins", "Publish").Should()
            .BeFalse("the sync licence was revoked, and the grant is what authorizes");
    }

    [Fact]
    public async Task AnExpiredLicence_DeniesEvenAValidToken()
    {
        var (id, _, hash) = await LicensedInstance("tok-expired");
        await Licenses().Issue(new SyncLicenseRequest
        {
            InstanceId = id,
            Source = "Plugins",
            PackageId = "Publish",
            IssuedByUserId = "platform-admin",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        }).Should().Emit();

        var caller = await Authenticator()
            .Authenticate($"Bearer {MintToken(id, hash, "Plugins/Publish")}")
            .Should().Emit();

        caller.Should().NotBeNull();
        caller!.Allows("Plugins", "Publish").Should().BeFalse("the licence term had already ended");
    }

    [Fact]
    public async Task ATokenForAnUnknownKey_DoesNotAuthenticate()
    {
        await LicensedInstance("tok-unknown", ("Plugins", "*"));
        var strayHash = InstanceKeys.Hash(InstanceKeys.Generate());

        var caller = await Authenticator()
            .Authenticate($"Bearer {MintToken("tok-unknown", strayHash)}")
            .Should().Emit();

        caller.Should().BeNull("the token routes by key hash, and that key was never issued");
    }

    [Fact]
    public async Task ATokenSignedWithAnotherKey_DoesNotAuthenticate()
    {
        var (id, _, hash) = await LicensedInstance("tok-forged", ("Plugins", "*"));
        var forged = SyncAccessToken.Mint(id, hash, [], DateTimeOffset.UtcNow,
            SyncAccessToken.DefaultLifetime, System.Text.Encoding.UTF8.GetBytes(new string('z', 40)));

        var caller = await Authenticator().Authenticate($"Bearer {forged}").Should().Emit();
        caller.Should().BeNull();
    }

    [Fact]
    public async Task TheDurableKeyStillAuthenticates_AndCarriesNoTokenScope()
    {
        // The token path must not disturb the key path it sits beside.
        var (_, rawKey, _) = await LicensedInstance("tok-durable", ("Plugins", "*"));

        var caller = await Authenticator().Authenticate($"Bearer {rawKey}").Should().Emit();

        caller.Should().NotBeNull();
        caller!.TokenScope.Should().BeNull("this caller presented the durable key");
        caller.Allows("Plugins", "Publish").Should().BeTrue();
    }
}
