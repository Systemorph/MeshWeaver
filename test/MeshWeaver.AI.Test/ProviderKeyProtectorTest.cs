#pragma warning disable CS1591

using MeshWeaver.Mesh.Security;
using System.Security.Cryptography;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit tests for <see cref="ProviderKeyProtector"/> — the AES-256-GCM
/// encryption-at-rest for ModelProvider API keys. Pure (no mesh): drives a
/// fixed <see cref="IMasterKeyProvider"/> directly.
/// </summary>
public class ProviderKeyProtectorTest
{
    private sealed class FixedMasterKey(byte[]? key) : IMasterKeyProvider
    {
        public byte[]? GetMasterKey() => key;
    }

    private static ProviderKeyProtector WithKey() =>
        new(new FixedMasterKey(RandomNumberGenerator.GetBytes(32)));

    private static ProviderKeyProtector WithoutKey() =>
        new(new FixedMasterKey(null));

    [Fact]
    public void Protect_ThenUnprotect_RoundTrips()
    {
        var p = WithKey();
        const string secret = "sk-ant-abc123-very-secret";

        var enc = p.Protect(secret);

        enc.Should().NotBeNull();
        enc!.Should().StartWith("enc:v1:");
        enc.Should().NotContain(secret);          // ciphertext, not the raw key
        p.Unprotect(enc).Should().Be(secret);     // round-trips
    }

    [Fact]
    public void Protect_IsIdempotent_NeverDoubleEncrypts()
    {
        var p = WithKey();
        var once = p.Protect("token");
        var twice = p.Protect(once);
        twice.Should().Be(once);
    }

    [Fact]
    public void Protect_UsesFreshNonce_DifferentCiphertextEachCall()
    {
        var p = WithKey();
        p.Protect("same").Should().NotBe(p.Protect("same"));
    }

    [Fact]
    public void NoMasterKey_RefusesToWrite_ButStillReads()
    {
        // This test asserted the OPPOSITE until 2026-08-24: that Protect passes the key through
        // when no master key is configured. That passthrough is the defect — it PERSISTED A RAW
        // PROVIDER KEY, and was found in production with a live OpenRouter key in cleartext in
        // Provider/OpenRouter node content. A missing master key is a configuration fault, not a
        // degraded mode, so the write fails and the caller hears about it.
        var p = WithoutKey();

        var refusal = Record.Exception(() => p.Protect("plain"));
        refusal.Should().BeOfType<InvalidOperationException>();
        refusal!.Message.Should().Contain(ConfigMasterKeyProvider.ConfigKey,
            "the error has to name the setting to configure, or it is unactionable");

        // Reads stay tolerant in the same state — that asymmetry is the point. A deployment
        // already holding legacy plaintext keeps working after an upgrade; it just cannot write
        // any more until it is configured. Fail on the way IN, tolerate on the way OUT.
        p.Unprotect("plain").Should().Be("plain");
    }

    [Fact]
    public void Unprotect_LegacyUntaggedPlaintext_PassesThrough()
    {
        // Pre-encryption rows: untagged values are returned verbatim so a
        // deployment that turns encryption on later keeps reading old keys.
        WithKey().Unprotect("legacy-plaintext-key").Should().Be("legacy-plaintext-key");
    }

    [Fact]
    public void Unprotect_WithWrongMasterKey_ReturnsNull()
    {
        var enc = WithKey().Protect("secret");
        var other = new ProviderKeyProtector(new FixedMasterKey(RandomNumberGenerator.GetBytes(32)));
        other.Unprotect(enc).Should().BeNull();   // GCM tag mismatch → null, never garbage
    }

    [Fact]
    public void NullAndEmpty_AreHandled()
    {
        var p = WithKey();
        p.Protect(null).Should().BeNull();
        p.Protect("").Should().Be("");
        p.Unprotect(null).Should().BeNull();
    }
}
