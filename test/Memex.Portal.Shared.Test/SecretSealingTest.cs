using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
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

    /// <summary>
    /// 🚨 The ephemeral public key is authenticated, not only its shared secret. Replacing the valid point
    /// <c>(x, y)</c> with its negation <c>(x, p − y)</c> is still a valid P-256 point and yields the SAME ECDH
    /// x-coordinate, hence the same AES key: unless the associated data binds the transmitted key, the tampered
    /// envelope would still authenticate.
    /// </summary>
    [Fact]
    public void ANegatedEphemeralPoint_DoesNotAuthenticate()
    {
        var (publicKey, privateKey) = SecretEnvelope.GenerateKeyPair();
        var envelope = SecretEnvelope.Seal(Secret, publicKey, "k1", Context);

        using var ephemeral = ECDiffieHellman.Create();
        ephemeral.ImportSubjectPublicKeyInfo(Convert.FromBase64String(envelope.EphemeralPublicKey), out _);
        var q = ephemeral.ExportParameters(false);
        var prime = System.Numerics.BigInteger.Parse(
            "0FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF", System.Globalization.NumberStyles.HexNumber);
        var y = new System.Numerics.BigInteger(q.Q.Y, isUnsigned: true, isBigEndian: true);
        var negated = (prime - y).ToByteArray(isUnsigned: true, isBigEndian: true);
        var negatedY = new byte[32];
        negated.CopyTo(negatedY, 32 - negated.Length);
        using var tampered = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = q.Q.X, Y = negatedY },
        });
        var tamperedKey = Convert.ToBase64String(tampered.ExportSubjectPublicKeyInfo());
        Assert.NotEqual(envelope.EphemeralPublicKey, tamperedKey);

        Assert.Equal(Secret, envelope.Unseal(privateKey, Context));
        Assert.Null((envelope with { EphemeralPublicKey = tamperedKey }).Unseal(privateKey, Context));
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
        var huge = Convert.ToBase64String(new byte[SecretEnvelope.MaxPlaintextBytes + 3]);
        Assert.Null(SecretEnvelope.TryParse((good with { Ciphertext = huge }).ToJson()));                      // oversized ciphertext
        // One byte over the limit has the same base64 length as the limit itself: the decoded length refuses it.
        Assert.Null((good with { Ciphertext = Convert.ToBase64String(new byte[SecretEnvelope.MaxPlaintextBytes + 1]) }).Unseal(privateKey, Context));
        Assert.Null(SecretEnvelope.TryParse((good with { KeyId = new string('k', 65) }).ToJson()));           // oversized key id
        Assert.Null(SecretEnvelope.TryParse((good with { Salt = Convert.ToBase64String(new byte[19]) }).ToJson()));
        Assert.Null((good with { Salt = Convert.ToBase64String(new byte[17]) }).Unseal(privateKey, Context));   // same base64 length, wrong size
        Assert.Null(SecretEnvelope.TryParse((good with { EphemeralPublicKey = new string('A', 4096) }).ToJson()));

        // Unseal refuses the same shapes even when the envelope was built directly, never via TryParse.
        Assert.Null((good with { Ciphertext = huge }).Unseal(privateKey, Context));
        Assert.Null((good with { Nonce = Convert.ToBase64String(new byte[13]) }).Unseal(privateKey, Context));
        Assert.Null((good with { Tag = Convert.ToBase64String(new byte[15]) }).Unseal(privateKey, Context));
        Assert.Equal(Secret, good.Unseal(privateKey, Context));
        Assert.NotNull(SecretEnvelope.TryParse(SecretEnvelope.Seal(new string('x', SecretEnvelope.MaxPlaintextBytes), publicKey, "k1", Context).ToJson()));
    }

    [Fact]
    public void AKeyIdItsOwnReaderWouldRefuse_IsRefusedAtSealing()
    {
        // Seal must never produce an envelope that TryParse/Unseal then refuse with the right key and context.
        var (publicKey, privateKey) = SecretEnvelope.GenerateKeyPair();
        Assert.Throws<ArgumentException>(() => SecretEnvelope.Seal(Secret, publicKey, "", Context));
        Assert.Throws<ArgumentException>(() => SecretEnvelope.Seal(Secret, publicKey, new string('k', 65), Context));
        var longest = SecretEnvelope.Seal(Secret, publicKey, new string('k', 64), Context);
        Assert.Equal(Secret, SecretEnvelope.TryParse(longest.ToJson())!.Unseal(privateKey, Context));
    }

    [Fact]
    public void MovingTheSeparatorBetweenKeyIdAndContext_DoesNotAuthenticate()
    {
        // The associated data must encode (key id, context) unambiguously: "k1|deployment-a" + "object" and
        // "k1" + "deployment-a|object" are different bindings, so the second must not open the first.
        var (publicKey, privateKey) = SecretEnvelope.GenerateKeyPair();
        var envelope = SecretEnvelope.Seal(Secret, publicKey, "k1|deployment-a", "object");
        Assert.Equal(Secret, envelope.Unseal(privateKey, "object"));
        Assert.Null((envelope with { KeyId = "k1" }).Unseal(privateKey, "deployment-a|object"));
    }

    [Fact]
    public void AReadThatIsNotAnAnswerOfAbsence_IsAFault_NeverANullKey()
    {
        // Only "absent" means "no key yet" (and so may lead to a create). A delete in flight or a read that could
        // not be answered must surface as a fault, or Resolve/Rotate would create a key over one that exists.
        var options = Mesh.JsonSerializerOptions;
        Assert.Null(SecretSealingKeyService.KeyFrom(NodeReadOutcome.Absent, options));
        Assert.Throws<InvalidOperationException>(() => SecretSealingKeyService.KeyFrom(NodeReadOutcome.DeleteInProgress, options));
        var unavailable = Assert.Throws<InvalidOperationException>(() =>
            SecretSealingKeyService.KeyFrom(NodeReadOutcome.Unavailable(new TimeoutException("owner did not answer")), options));
        Assert.IsType<TimeoutException>(unavailable.InnerException);
        var key = new SecretSealingKey { CurrentId = "k1", CurrentPublicKey = "pub", ProtectedCurrentPrivateKey = "enc:x" };
        var present = NodeReadOutcome.Present(new MeshNode(SecretSealingKeys.Id, SecretSealingKeys.Namespace) { Content = key });
        Assert.Equal("k1", SecretSealingKeyService.KeyFrom(present, options)!.CurrentId);
        // A node that IS there but does not read as a key is a fault too: null would let Rotate/Resolve create a
        // key at a path that already holds a node.
        var notAKey = NodeReadOutcome.Present(new MeshNode(SecretSealingKeys.Id, SecretSealingKeys.Namespace) { Content = null });
        Assert.Throws<InvalidOperationException>(() => SecretSealingKeyService.KeyFrom(notAKey, options));
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
        k3.Retired!.Select(r => r.Id).Should().Equal(["k0", "k1", "k2"],
            "a retired key past its deadline stays as a tombstone, so its first deadline still decides");
        k3.Retired!.Single(r => r.Id == "k0").ProtectedPrivateKey.Should().BeEmpty(
            "the next rotation after its deadline drops the expired key's material");
        k3.ProtectedPrivateKeyFor("k0", now.Add(grace).AddMinutes(1)).Should().BeNull();
    }

    /// <summary>
    /// 🚨 A rotation's update lambda runs on the WRITER's mirror (<c>MeshNodeStreamHandle.UpdateRemote</c>), not at
    /// the owner: what reaches the owner is the merge patch of what the lambda changed plus the writer's base values,
    /// which the owner merges three-way (<see cref="MeshNodePatchMerge"/>). A rotation that read K0 and is delivered
    /// after the owner already went K0 → KA → KC must not take KA out of the retired keys: everything sealed to KA
    /// would stop opening before its deadline. Driven through the REAL diff and the REAL owner merge.
    /// </summary>
    [Fact]
    public void ARotationDelayedBehindTwoOthers_KeepsEveryKeyRetiredSinceItsMirrorWasRead()
    {
        var options = Mesh.JsonSerializerOptions;
        var now = DateTimeOffset.UtcNow;
        var grace = SecretSealingKeys.RotationGrace;
        SecretSealingKey Minted(string id) => new()
        {
            CurrentId = id, CurrentPublicKey = "pub-" + id, ProtectedCurrentPrivateKey = "enc:" + id,
            CurrentIssuedAt = now, RotateAfter = now.AddDays(90),
        };
        JsonObject Json(SecretSealingKey key) => MeshNodeStreamHandle.ToJsonObject(
            new MeshNode(SecretSealingKeys.Id, SecretSealingKeys.Namespace) { NodeType = SecretSealingKeys.NodeType, Content = key },
            options);

        // The delayed writer's mirror still holds the key as minted, and overdue.
        var k0 = Minted("k0") with { RotateAfter = now.AddMinutes(-1) };
        // Meanwhile the owner rotated twice.
        var live = k0.RotatedTo(Minted("kA"), now, grace).RotatedTo(Minted("kC"), now, grace);
        // The delayed writer's lambda, against its mirror.
        var proposed = SecretSealingKeyService.RotateAtOwner(k0, Minted("kX"), k => SecretSealingKeyService.IsDue(k, now), now);

        var mirror = Json(k0);
        var patch = MeshNodeStreamHandle.ComputeMergePatchDiff(mirror, Json(proposed));
        var owner = Json(live);
        MeshNodePatchMerge.Apply(owner, patch, MeshNodePatchMerge.ExtractBaseValues(mirror, patch));

        var merged = owner.Deserialize<MeshNode>(options)!.ContentAs<SecretSealingKey>(options)!;
        merged.ProtectedPrivateKeyFor("kA", now).Should().Be("enc:kA",
            "KA was retired at the owner after the delayed writer read its mirror, and is still inside its window");
        merged.ProtectedPrivateKeyFor("k0", now).Should().Be("enc:k0");
    }

    /// <summary>
    /// 🚨 Two rotations computed from K0 at DIFFERENT times each retire K0, with different deadlines. The owner's
    /// array merge compares whole elements, so both entries land. K0 retired ONCE: its first deadline holds, and
    /// the later writer's entry must not extend it. Driven through the REAL diff and the REAL owner merge.
    /// </summary>
    [Fact]
    public void TwoRotationsOfTheSameKeyAtDifferentTimes_KeepTheFirstDeadline()
    {
        var options = Mesh.JsonSerializerOptions;
        var now = DateTimeOffset.UtcNow;
        var later = now.AddDays(1);
        var grace = SecretSealingKeys.RotationGrace;
        SecretSealingKey Minted(string id, DateTimeOffset at) => new()
        {
            CurrentId = id, CurrentPublicKey = "pub-" + id, ProtectedCurrentPrivateKey = "enc:" + id,
            CurrentIssuedAt = at, RotateAfter = at.AddDays(90),
        };
        JsonObject Json(SecretSealingKey key) => MeshNodeStreamHandle.ToJsonObject(
            new MeshNode(SecretSealingKeys.Id, SecretSealingKeys.Namespace) { NodeType = SecretSealingKeys.NodeType, Content = key },
            options);

        var k0 = Minted("k0", now.AddDays(-90)) with { RotateAfter = now.AddMinutes(-1) };
        // The owner rotated K0 → KA at `now`.
        var live = k0.RotatedTo(Minted("kA", now), now, grace);
        // A writer whose mirror still holds K0 rotates it a day LATER.
        var proposed = SecretSealingKeyService.RotateAtOwner(k0, Minted("kX", later), k => SecretSealingKeyService.IsDue(k, later), later);

        var mirror = Json(k0);
        var patch = MeshNodeStreamHandle.ComputeMergePatchDiff(mirror, Json(proposed));
        var owner = Json(live);
        MeshNodePatchMerge.Apply(owner, patch, MeshNodePatchMerge.ExtractBaseValues(mirror, patch));

        var merged = owner.Deserialize<MeshNode>(options)!.ContentAs<SecretSealingKey>(options)!;
        merged.ProtectedPrivateKeyFor("k0", now.Add(grace).AddMinutes(-1)).Should().Be("enc:k0");
        merged.ProtectedPrivateKeyFor("k0", now.Add(grace)).Should().BeNull(
            "K0 retired at the owner first; a later writer's retirement of the same key must not extend its window");

        // And the next rotation keeps ONE entry per key id, the first deadline.
        var next = merged.RotatedTo(Minted("kN", now.AddDays(2)), now.AddDays(2), grace);
        next.Retired.Where(r => r.Id == "k0").Should().ContainSingle().Which.ValidUntil.Should().Be(now.Add(grace));
    }

    /// <summary>
    /// 🚨 Cleaning up an expired key must not re-open it. The owner holds TWO retirements of K0 (days 7 and 8: two
    /// rotations of K0 computed at different times both landed); a writer whose mirror holds only the first rotates
    /// at day 7.5. The array merge removes only what the writer saw, so if the cleanup DROPPED the expired first
    /// entry, the unseen day-8 entry would become the earliest and K0 would open again until day 8. Driven through
    /// the REAL diff and the REAL owner merge.
    /// </summary>
    [Fact]
    public void CleaningUpAnExpiredKey_FromAPartialMirror_DoesNotReopenIt()
    {
        var options = Mesh.JsonSerializerOptions;
        var t0 = DateTimeOffset.UtcNow;
        var first = new RetiredSealingKey("k0", "enc:k0", t0.AddDays(7));
        var second = new RetiredSealingKey("k0", "enc:k0", t0.AddDays(8));
        var kA = Minted("kA", t0) with { RotateAfter = t0.AddDays(7).AddHours(1) };
        var live = kA with { Retired = [first, second] };
        var mirror = kA with { Retired = [first] };
        var cleanupAt = t0.AddDays(7.5);

        var proposed = SecretSealingKeyService.RotateAtOwner(mirror, Minted("kB", cleanupAt), k => SecretSealingKeyService.IsDue(k, cleanupAt), cleanupAt);
        var merged = MergedAtOwner(options, mirror, proposed, live);

        merged.CurrentId.Should().Be("kB");
        merged.ProtectedPrivateKeyFor("kA", cleanupAt).Should().Be("enc:kA");
        merged.ProtectedPrivateKeyFor("k0", cleanupAt).Should().BeNull(
            "K0's first deadline (day 7) has passed; a retirement the cleaning writer never saw must not re-open it");
    }

    /// <summary>
    /// 🚨 A retirement that arrives AFTER the cleanup must not re-open the key either. The owner already cleaned up
    /// K0 (first deadline day 7); a delayed rotation computed from K0 on day 1 lands a K0 entry ending day 8.
    /// </summary>
    [Fact]
    public void ARetirementDelayedPastTheCleanup_DoesNotReopenTheKey()
    {
        var options = Mesh.JsonSerializerOptions;
        var t0 = DateTimeOffset.UtcNow;
        var grace = SecretSealingKeys.RotationGrace;
        var k0 = Minted("k0", t0.AddDays(-90)) with { RotateAfter = t0.AddMinutes(-1) };
        // The owner rotated K0 → KA at t0, and KA → KB at day 7.5, which cleaned K0 up.
        var kA = k0.RotatedTo(Minted("kA", t0) with { RotateAfter = t0.AddDays(7).AddHours(1) }, t0, grace);
        var cleanupAt = t0.AddDays(7.5);
        var live = kA.RotatedTo(Minted("kB", cleanupAt), cleanupAt, grace);
        // A writer whose mirror held K0 computed its rotation on day 1; it lands now.
        var delayedAt = t0.AddDays(1);
        var proposed = SecretSealingKeyService.RotateAtOwner(k0, Minted("kX", delayedAt), k => SecretSealingKeyService.IsDue(k, delayedAt), delayedAt);
        var merged = MergedAtOwner(options, k0, proposed, live);

        merged.ProtectedPrivateKeyFor("k0", cleanupAt).Should().BeNull(
            "K0 retired once, at t0; a retirement that arrives after its cleanup must not give it a later deadline");
    }

    private static SecretSealingKey Minted(string id, DateTimeOffset at) => new()
    {
        CurrentId = id, CurrentPublicKey = "pub-" + id, ProtectedCurrentPrivateKey = "enc:" + id,
        CurrentIssuedAt = at, RotateAfter = at.AddDays(90),
    };

    private static SecretSealingKey MergedAtOwner(JsonSerializerOptions options, SecretSealingKey mirror,
        SecretSealingKey proposed, SecretSealingKey live)
    {
        JsonObject Json(SecretSealingKey key) => MeshNodeStreamHandle.ToJsonObject(
            new MeshNode(SecretSealingKeys.Id, SecretSealingKeys.Namespace) { NodeType = SecretSealingKeys.NodeType, Content = key },
            options);
        var mirrorJson = Json(mirror);
        var patch = MeshNodeStreamHandle.ComputeMergePatchDiff(mirrorJson, Json(proposed));
        var owner = Json(live);
        MeshNodePatchMerge.Apply(owner, patch, MeshNodePatchMerge.ExtractBaseValues(mirrorJson, patch));
        return owner.Deserialize<MeshNode>(options)!.ContentAs<SecretSealingKey>(options)!;
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
        var sealedToThird = SecretEnvelope.Seal(Secret, Convert.FromBase64String(third.PublicKey), third.KeyId, Context);
        (await service.Unseal(sealedToFirst.ToJson(), Context).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken))
            .Should().Be(Secret, "two rotations inside one window do not drop the first outgoing key before its deadline");

        // Past the window the outgoing keys no longer open: age them out through the node's own stream.
        await Mesh.GetWorkspace().GetMeshNodeStream(SecretSealingKeys.Path)
            .Update(current => current with
            {
                Content = current.ContentAs<SecretSealingKey>(Mesh.JsonSerializerOptions) is { } there
                    ? there with { Retired = there.Retired.Select(r => r with { ValidUntil = DateTimeOffset.UtcNow.AddMinutes(-1) }).ToImmutableList() }
                    : current.Content,
            })
            .Take(1).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken);
        (await service.Unseal(sealedToFirst.ToJson(), Context).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken))
            .Should().BeNull("the outgoing key is accepted for a bounded window only");
        (await service.Unseal(sealedToSecond.ToJson(), Context).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken))
            .Should().BeNull("the second key is retired too, and its window is over");
        (await service.Unseal(sealedToThird.ToJson(), Context).Timeout(TimeSpan.FromSeconds(60)).Await(TestContext.Current.CancellationToken)).Should().Be(Secret);
    }
}
