using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Security.Cryptography;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Data;
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
/// The secret envelope (ECDH P-256 + HKDF-SHA256 + AES-256-GCM, .NET built-ins) and the control instance's
/// sealing key: sealing round-trips; the context, the key id, the version and the bytes are all authenticated; the
/// private half is only ever <c>enc:</c> ciphertext in the node; the public half is served to an authenticated
/// installation and to nobody else; rotation keeps the outgoing key for a bounded window.
/// </summary>
public class SecretSealingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Secret = "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ere";
    private const string Context = "memex-cloud|Google--OAuth--ClientSecret";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    // ── the envelope, pure ──────────────────────────────────────────────────

    [Fact]
    public void ASealedSecret_OpensWithTheRecipientsPrivateKey_ForTheSameContext()
    {
        var (publicKey, privateKey) = SecretEnvelope.GenerateKeyPair();
        var envelope = SecretEnvelope.Seal(Secret, publicKey, "k1", Context);
        Assert.Equal(Secret, envelope.Unseal(privateKey, Context));
        Assert.DoesNotContain(Secret, envelope.ToJson());
        Assert.DoesNotContain(Secret, envelope.ToString());
        Assert.Equal(1, envelope.Version);
        Assert.Equal("k1", envelope.KeyId);
    }

    [Fact]
    public void TwoSealsOfTheSameSecret_Differ_BecauseTheSenderKeyIsEphemeral()
    {
        var (publicKey, _) = SecretEnvelope.GenerateKeyPair();
        var a = SecretEnvelope.Seal(Secret, publicKey, "k1", Context);
        var b = SecretEnvelope.Seal(Secret, publicKey, "k1", Context);
        Assert.NotEqual(a.Ciphertext, b.Ciphertext);
        Assert.NotEqual(a.EphemeralPublicKey, b.EphemeralPublicKey);
    }

    [Fact]
    public void ADifferentContext_DifferentKey_OrTamperedBytes_NeverOpen()
    {
        var (publicKey, privateKey) = SecretEnvelope.GenerateKeyPair();
        var (_, otherPrivate) = SecretEnvelope.GenerateKeyPair();
        var envelope = SecretEnvelope.Seal(Secret, publicKey, "k1", Context);

        Assert.Null(envelope.Unseal(privateKey, "other-deployment|Google--OAuth--ClientSecret"));   // replay onto another deployment
        Assert.Null(envelope.Unseal(privateKey, "memex-cloud|Another--Object"));                     // …or another object
        Assert.Null(envelope.Unseal(otherPrivate, Context));                                         // wrong key
        Assert.Null((envelope with { KeyId = "k2" }).Unseal(privateKey, Context));                   // key id is authenticated
        Assert.Null((envelope with { Version = 2 }).Unseal(privateKey, Context));                    // unknown version
        var bytes = Convert.FromBase64String(envelope.Ciphertext);
        bytes[0] ^= 1;
        Assert.Null((envelope with { Ciphertext = Convert.ToBase64String(bytes) }).Unseal(privateKey, Context));
        var tag = Convert.FromBase64String(envelope.Tag);
        tag[0] ^= 1;
        Assert.Null((envelope with { Tag = Convert.ToBase64String(tag) }).Unseal(privateKey, Context));
        Assert.Null((envelope with { EphemeralPublicKey = "not base64" }).Unseal(privateKey, Context));
    }

    [Fact]
    public void AnEmptyOrOversizedSecret_IsRefusedAtSealing_AndGarbageParsesToNull()
    {
        var (publicKey, _) = SecretEnvelope.GenerateKeyPair();
        Assert.Throws<ArgumentException>(() => SecretEnvelope.Seal("", publicKey, "k1", Context));
        Assert.Throws<ArgumentException>(() => SecretEnvelope.Seal(new string('x', SecretEnvelope.MaxPlaintextBytes + 1), publicKey, "k1", Context));
        Assert.Null(SecretEnvelope.TryParse("{"));
        Assert.Null(SecretEnvelope.TryParse("{\"v\":1}"));
        Assert.Null(SecretEnvelope.TryParse(null));
        var (pub, _) = SecretEnvelope.GenerateKeyPair();
        Assert.NotNull(SecretEnvelope.TryParse(SecretEnvelope.Seal(Secret, pub, "k1", Context).ToJson()));
    }

    [Fact]
    public void HostileEnvelopes_AreRefusedBeforeAnythingIsDecoded()
    {
        var (publicKey, privateKey) = SecretEnvelope.GenerateKeyPair();
        var good = SecretEnvelope.Seal(Secret, publicKey, "k1", Context);

        Assert.Null(SecretEnvelope.TryParse(new string(' ', SecretEnvelope.MaxWireChars) + good.ToJson()));   // oversized wire
        var huge = Convert.ToBase64String(new byte[SecretEnvelope.MaxPlaintextBytes + 1]);
        Assert.Null(SecretEnvelope.TryParse((good with { Ciphertext = huge }).ToJson()));                      // oversized ciphertext
        Assert.Null(SecretEnvelope.TryParse((good with { KeyId = new string('k', 65) }).ToJson()));           // oversized key id
        Assert.Null(SecretEnvelope.TryParse((good with { Salt = Convert.ToBase64String(new byte[17]) }).ToJson()));
        Assert.Null(SecretEnvelope.TryParse((good with { EphemeralPublicKey = new string('A', 4096) }).ToJson()));

        // Unseal refuses the same shapes even when the envelope was built directly, never via TryParse.
        Assert.Null((good with { Ciphertext = huge }).Unseal(privateKey, Context));
        Assert.Null((good with { Nonce = Convert.ToBase64String(new byte[13]) }).Unseal(privateKey, Context));
        Assert.Null((good with { Tag = Convert.ToBase64String(new byte[15]) }).Unseal(privateKey, Context));
        Assert.Equal(Secret, good.Unseal(privateKey, Context));
        Assert.NotNull(SecretEnvelope.TryParse(SecretEnvelope.Seal(new string('x', SecretEnvelope.MaxPlaintextBytes), publicKey, "k1", Context).ToJson()));
    }

    [Fact]
    public void ARotationThatLostTheRace_AdoptsTheOneThatLanded_InsteadOfReplacingIt()
    {
        var now = DateTimeOffset.UtcNow;
        var k0 = new SecretSealingKey { CurrentId = "k0", ProtectedCurrentPrivateKey = "enc:k0", RotateAfter = now.AddMinutes(-1) };
        var kA = new SecretSealingKey { CurrentId = "kA", ProtectedCurrentPrivateKey = "enc:kA", RotateAfter = now.AddDays(90) };
        var kB = new SecretSealingKey { CurrentId = "kB", ProtectedCurrentPrivateKey = "enc:kB", RotateAfter = now.AddDays(90) };
        bool Due(SecretSealingKey k) => SecretSealingKeyService.IsDue(k, now);

        var first = SecretSealingKeyService.RotateAtOwner(k0, kA, Due, now);
        var second = SecretSealingKeyService.RotateAtOwner(first, kB, Due, now);

        first.CurrentId.Should().Be("kA");
        second.Should().BeSameAs(first, "the second request read the same overdue K0, but at the owner K0 is already rotated: KA stays");
        second.ProtectedPrivateKeyFor("k0", now).Should().Be("enc:k0", "the outgoing key is retired, not dropped");
    }

    [Fact]
    public void TwoRotationsInsideOneWindow_KeepEveryOutgoingKeyUntilItsOwnDeadline()
    {
        var now = DateTimeOffset.UtcNow;
        var grace = SecretSealingKeys.RotationGrace;
        var k0 = new SecretSealingKey { CurrentId = "k0", ProtectedCurrentPrivateKey = "enc:k0" };
        var k1 = k0.RotatedTo(new SecretSealingKey { CurrentId = "k1", ProtectedCurrentPrivateKey = "enc:k1" }, now, grace);
        var k2 = k1.RotatedTo(new SecretSealingKey { CurrentId = "k2", ProtectedCurrentPrivateKey = "enc:k2" }, now.AddDays(1), grace);

        k2.ProtectedPrivateKeyFor("k0", now.AddDays(2)).Should().Be("enc:k0", "K0 is inside its own window although two rotations happened");
        k2.ProtectedPrivateKeyFor("k1", now.AddDays(2)).Should().Be("enc:k1");
        k2.ProtectedPrivateKeyFor("k2", now.AddDays(2)).Should().Be("enc:k2");
        k2.ProtectedPrivateKeyFor("k0", now.Add(grace)).Should().BeNull("K0's window ends at its own deadline");
        k2.ProtectedPrivateKeyFor("k1", now.Add(grace)).Should().Be("enc:k1", "K1's window runs a day longer");
        var k3 = k2.RotatedTo(new SecretSealingKey { CurrentId = "k3", ProtectedCurrentPrivateKey = "enc:k3" }, now.Add(grace).AddMinutes(1), grace);
        k3.Retired!.Select(r => r.Id).Should().Equal(["k1", "k2"], "a retired key past its deadline is dropped at the next rotation");
    }

    [Fact]
    public async Task ALookupThatFails_IsAnswered503_NotAnUnhandledFault()
    {
        var result = await SealKeyEndpoints.Answer(Observable.Throw<SealingPublicKey?>(new InvalidOperationException("no master key")), null)
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode.Should().Be(503);
        var missing = await SealKeyEndpoints.Answer(Observable.Return<SealingPublicKey?>(null), null)
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)missing).StatusCode.Should().Be(404);
    }

    // ── the control instance's key, on a real mesh ──────────────────────────

    private async Task<WebApplication> Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(Mesh.ServiceProvider.GetRequiredService<InstanceRegistryAuthenticator>());
        var app = builder.Build();
        app.MapSealKey();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private Task<InstanceRegistrationResult> Register(string id)
    {
        var service = new MeshWeaverInstanceService(
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(), Mesh,
            Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());
        return service.Register("owner", "Owner", "owner@test.com", id, id).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 180_000)]
    public async Task TheSealKey_IsPublicAndReadOnly_ARequestNeverMintsIt()
    {
        await using var app = await Start();
        var client = app.GetTestClient();

        using (var before = await client.GetAsync(SealKeyPayloads.Route, TestContext.Current.CancellationToken))
            before.StatusCode.Should().Be(HttpStatusCode.NotFound, "until the control plane mints the pair there is nothing to publish");
        (await Mesh.GetMeshNode(SecretSealingKeys.Path, TimeSpan.FromSeconds(10)).Take(1).Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken))
            .Should().BeNull("an anonymous request must not provoke the mint");

        var service = Mesh.ServiceProvider.GetRequiredService<SecretSealingKeyService>();
        var minted = await service.PublicKey().Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken);

        using var response = await client.GetAsync(SealKeyPayloads.Route, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var key = (await response.Content.ReadFromJsonAsync<SealKeyPayloads.Response>(SealKeyPayloads.Json, TestContext.Current.CancellationToken))!;
        key.KeyId.Should().Be(minted.KeyId);
        key.PublicKey.Should().Be(minted.PublicKey);
        key.Algorithm.Should().Be(SealKeyPayloads.Algorithm);
        key.Version.Should().Be(SecretEnvelope.CurrentVersion);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("rivate", "only the public half is ever served");

        // Seal to the served key; only the control instance opens it.
        var envelope = SecretEnvelope.Seal(Secret, Convert.FromBase64String(key.PublicKey), key.KeyId, Context);
        (await service.Unseal(envelope.ToJson(), Context).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken)).Should().Be(Secret);
        (await service.Unseal(envelope.ToJson(), "other|Object").Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken)).Should().BeNull();
        (await service.Unseal("garbage", Context).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact(Timeout = 180_000)]
    public async Task ThePrivateHalf_IsOnlyEverCiphertextInTheNode_AndTheSecretNeverAppearsInIt()
    {
        var service = Mesh.ServiceProvider.GetRequiredService<SecretSealingKeyService>();
        var key = await service.PublicKey().Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken);
        var node = await Mesh.GetMeshNode(SecretSealingKeys.Path, TimeSpan.FromSeconds(10)).Take(1).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken);
        var stored = node!.ContentAs<SecretSealingKey>(Mesh.JsonSerializerOptions)!;
        stored.CurrentId.Should().Be(key.KeyId);
        stored.CurrentPublicKey.Should().Be(key.PublicKey);
        stored.ProtectedCurrentPrivateKey.Should().StartWith("enc:", "the private half is protected by the platform key protector");
        // A raw PKCS#8 P-256 key would start with MIGH…/MIIB… in base64: the stored value is not that.
        stored.ProtectedCurrentPrivateKey.Should().NotStartWith("MI");
    }

    [Fact(Timeout = 180_000)]
    public async Task Rotation_KeepsTheOutgoingKeyForABoundedWindow_ThenOnlyTheNewOneOpens()
    {
        var service = Mesh.ServiceProvider.GetRequiredService<SecretSealingKeyService>();
        var first = await service.PublicKey().Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken);
        var sealedToFirst = SecretEnvelope.Seal(Secret, Convert.FromBase64String(first.PublicKey), first.KeyId, Context);

        var second = await service.Rotate("test").Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken);
        second.KeyId.Should().NotBe(first.KeyId);
        (await service.Unseal(sealedToFirst.ToJson(), Context).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken))
            .Should().Be(Secret, "a value sealed just before the rotation still opens inside the window");
        var sealedToSecond = SecretEnvelope.Seal(Secret, Convert.FromBase64String(second.PublicKey), second.KeyId, Context);
        (await service.Unseal(sealedToSecond.ToJson(), Context).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken)).Should().Be(Secret);

        // A second rotation inside the window keeps the first key too.
        var third = await service.Rotate("test").Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken);
        third.KeyId.Should().NotBe(second.KeyId);
        (await service.Unseal(sealedToFirst.ToJson(), Context).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken))
            .Should().Be(Secret, "two rotations inside one window do not drop the first outgoing key before its deadline");

        // Past the window the outgoing keys no longer open: age them out through the node's own stream.
        await Mesh.GetWorkspace().GetMeshNodeStream(SecretSealingKeys.Path)
            .Update(current => current with
            {
                Content = current.ContentAs<SecretSealingKey>(Mesh.JsonSerializerOptions) is { } there
                    ? there with { Retired = there.Retired?.Select(r => r with { ValidUntil = DateTimeOffset.UtcNow.AddMinutes(-1) }).ToImmutableList() }
                    : current.Content,
            })
            .Take(1).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken);
        (await service.Unseal(sealedToFirst.ToJson(), Context).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken))
            .Should().BeNull("the outgoing key is accepted for a bounded window only");
        (await service.Unseal(sealedToSecond.ToJson(), Context).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken)).Should().Be(Secret);
    }
}
