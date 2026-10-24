using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Security.Cryptography;
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
    public async Task TheSealKey_IsServedToARegisteredInstance_AndToNobodyElse_AndMintsNothingForAnAnonymousCaller()
    {
        await using var app = await Start();
        var client = app.GetTestClient();

        using (var anonymous = await client.GetAsync(SealKeyPayloads.Route, TestContext.Current.CancellationToken))
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        (await Mesh.GetMeshNode(SecretSealingKeys.Path, TimeSpan.FromSeconds(10)).Take(1).Timeout(TimeSpan.FromSeconds(30)).Await(TestContext.Current.CancellationToken))
            .Should().BeNull("an anonymous request must not provoke the mint");

        var registered = await Register("sealer");
        using var request = new HttpRequestMessage(HttpMethod.Get, SealKeyPayloads.Route);
        request.Headers.TryAddWithoutValidation("Authorization", InstanceKeys.AuthorizationHeader(registered.RawKey));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var key = (await response.Content.ReadFromJsonAsync<SealKeyPayloads.Response>(SealKeyPayloads.Json, TestContext.Current.CancellationToken))!;
        key.Algorithm.Should().Be(SealKeyPayloads.Algorithm);
        key.Version.Should().Be(SecretEnvelope.CurrentVersion);

        // Seal to the served key; only the control instance opens it.
        var envelope = SecretEnvelope.Seal(Secret, Convert.FromBase64String(key.PublicKey), key.KeyId, Context);
        var service = Mesh.ServiceProvider.GetRequiredService<SecretSealingKeyService>();
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

        // Past the window the outgoing key no longer opens: age the stored previous key out.
        var node = await Mesh.GetMeshNode(SecretSealingKeys.Path, TimeSpan.FromSeconds(10)).Take(1).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken);
        var expired = node!.ContentAs<SecretSealingKey>(Mesh.JsonSerializerOptions)! with { PreviousValidUntil = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateOrUpdateNode(node! with { Content = expired })
            .Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken);
        (await service.Unseal(sealedToFirst.ToJson(), Context).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken))
            .Should().BeNull("the outgoing key is accepted for a bounded window only");
        (await service.Unseal(sealedToSecond.ToJson(), Context).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken)).Should().Be(Secret);
    }
}
