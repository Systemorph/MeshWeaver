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
using MeshWeaver.Fixture;

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
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog();

    private SyncTokenSigningKeyService Keys() =>
        Mesh.ServiceProvider.GetRequiredService<SyncTokenSigningKeyService>();

    /// <summary>The registry's signing key, minted from the mesh on first ask — no configuration.</summary>
    private async Task<byte[]> SigningKey() =>
        (await Keys().Resolve().Should().Emit()).Current;

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

    private async Task<string> MintToken(string instanceId, string keyHash, params string[] scope) =>
        SyncAccessToken.Mint(instanceId, keyHash, scope, DateTimeOffset.UtcNow,
            SyncAccessToken.DefaultLifetime, await SigningKey());

    [Fact]
    public async Task AToken_AuthenticatesAndResolvesToItsInstance()
    {
        var (id, _, hash) = await LicensedInstance("tok-a", ("Plugins", "*"));

        var caller = await Authenticator()
            .Authenticate($"Bearer {await MintToken(id, hash)}")
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
            .Authenticate($"Bearer {await MintToken(id, hash, "Plugins/Publish")}")
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
            .Authenticate($"Bearer {await MintToken(id, hash, "Plugins/*", "Education/DataModeling")}")
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
        var token = await MintToken(id, hash, "Plugins/Publish");

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
            .Authenticate($"Bearer {await MintToken(id, hash, "Plugins/Publish")}")
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
            .Authenticate($"Bearer {await MintToken("tok-unknown", strayHash)}")
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

    [Fact]
    public async Task TheSigningKeyIsMintedOnceAndSharedByEveryReplica()
    {
        // 🚨 THE uniqueness property, and the reason the key is a mesh NODE rather than a lock or a
        // per-process secret: two replicas asking independently must end up with the SAME key, or a
        // token minted on one fails on the other. Two separate service instances stand in for two
        // replicas — they share only the mesh.
        var replicaA = new SyncTokenSigningKeyService(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<SyncTokenSigningKeyService>>());
        var replicaB = new SyncTokenSigningKeyService(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<SyncTokenSigningKeyService>>());

        // Both are STARTED before either finishes — a sequential A-then-B would only prove that a
        // second reader finds an existing node, never that the create COLLISION resolves to one key.
        var raceA = replicaA.Resolve().FirstAsync().Await();
        var raceB = replicaB.Resolve().FirstAsync().Await();
        var raced = await Task.WhenAll(raceA, raceB);
        var fromA = raced[0];
        var fromB = raced[1];

        Convert.ToBase64String(fromB.Current).Should().Be(
            Convert.ToBase64String(fromA.Current),
            "the loser of the mint race adopts the winner's key instead of overwriting it");

        // And the practical consequence: a token minted by one verifies on the other.
        var (id, _, hash) = await LicensedInstance("tok-shared", ("Plugins", "Publish"));
        var minted = SyncAccessToken.Mint(id, hash, ["Plugins/Publish"], DateTimeOffset.UtcNow,
            SyncAccessToken.DefaultLifetime, fromA.Current);
        fromB.Verify(minted, DateTimeOffset.UtcNow).Should().NotBeNull();
    }

    [Fact]
    public async Task RotationKeepsTokensInFlightWorking()
    {
        // A rotation that simply replaced the key would invalidate every token minted in the minutes
        // before it — mid-run, for every consumer.
        var keys = Keys();
        var before = await keys.Resolve().Should().Emit();

        var (id, _, hash) = await LicensedInstance("tok-rotate", ("Plugins", "Publish"));
        var mintedBeforeRotation = SyncAccessToken.Mint(
            id, hash, ["Plugins/Publish"], DateTimeOffset.UtcNow,
            SyncAccessToken.DefaultLifetime, before.Current);

        var after = await keys.Rotate("test").Should().Emit();

        Convert.ToBase64String(after.Current).Should().NotBe(
            Convert.ToBase64String(before.Current), "rotation mints fresh material");
        Convert.ToBase64String(after.Previous!).Should().Be(
            Convert.ToBase64String(before.Current), "the outgoing key is retained, not discarded");
        after.Verify(mintedBeforeRotation, DateTimeOffset.UtcNow).Should()
            .NotBeNull("a token minted just before the rotation must still verify");

        // New tokens are signed with the NEW key, and still verify.
        var mintedAfter = SyncAccessToken.Mint(
            id, hash, ["Plugins/Publish"], DateTimeOffset.UtcNow,
            SyncAccessToken.DefaultLifetime, after.Current);
        after.Verify(mintedAfter, DateTimeOffset.UtcNow).Should().NotBeNull();
    }

    [Fact]
    public async Task TheAuthenticatorPicksUpARotation()
    {
        // The authenticator caches key material briefly; a token signed with the rotated-in key must
        // authenticate, which is what proves the key is read from the mesh and not from a captured
        // configuration value.
        var (id, _, hash) = await LicensedInstance("tok-rot-auth", ("Plugins", "Publish"));
        var rotated = await Keys().Rotate("test").Should().Emit();

        var token = SyncAccessToken.Mint(id, hash, ["Plugins/Publish"], DateTimeOffset.UtcNow,
            SyncAccessToken.DefaultLifetime, rotated.Current);

        var caller = await Authenticator().Authenticate($"Bearer {token}").Should().Emit();
        caller.Should().NotBeNull();
        caller!.Allows("Plugins", "Publish").Should().BeTrue();
    }

    [Fact]
    public async Task AnUnauthenticatedTokenDoesNotMintAKey()
    {
        // Minting is a node write. If the VERIFY path minted, an anonymous caller sending junk could
        // provoke one — and it would be pointless anyway, since a token cannot verify against a key
        // created after it was signed.
        var keys = Keys();
        (await keys.Existing().Should().Emit()).Should().BeNull("nothing has minted yet");

        var caller = await Authenticator()
            .Authenticate("Bearer mwa_not-a-real-token.nope")
            .Should().Emit();

        caller.Should().BeNull();
        (await keys.Existing().Should().Emit()).Should()
            .BeNull("verifying a junk token must not have written a signing key");
    }
}
