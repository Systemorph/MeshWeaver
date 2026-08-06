using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the instance-key contract: how a key is minted, hashed, and pulled back out of an
/// <c>Authorization</c> header. The prefix separation from personal API tokens is the property that
/// keeps an instance credential from ever being mistaken for a user credential, so it is asserted
/// explicitly rather than left to the constant.
/// </summary>
public class InstanceKeysTest
{
    [Fact]
    public void GeneratedKey_CarriesTheInstancePrefix_AndIsUrlSafe()
    {
        var key = InstanceKeys.Generate();
        Assert.StartsWith("mwi_", key);
        // URL-safe base64: no +, /, or = survives the transform.
        Assert.DoesNotContain('+', key);
        Assert.DoesNotContain('/', key);
        Assert.DoesNotContain('=', key);
    }

    [Fact]
    public void GeneratedKeys_AreDistinct()
    {
        var keys = Enumerable.Range(0, 50).Select(_ => InstanceKeys.Generate()).ToHashSet();
        Assert.Equal(50, keys.Count);
    }

    [Fact]
    public void InstancePrefix_IsDisjointFromThePersonalTokenPrefix()
    {
        // A personal token is mw_; an instance key is mwi_. Neither is a prefix of the other, so
        // neither validator can accept the other's credential. If these ever collapse, a leaked
        // instance key could be replayed as its owner against the whole mesh API.
        Assert.NotEqual(ValidateTokenRequest.TokenPrefix, InstanceKeys.KeyPrefix);
        Assert.False(InstanceKeys.KeyPrefix.StartsWith(ValidateTokenRequest.TokenPrefix, StringComparison.Ordinal));
        Assert.False(ValidateTokenRequest.TokenPrefix.StartsWith(InstanceKeys.KeyPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void PersonalToken_IsNotAcceptedAsAnInstanceKey()
    {
        // The decisive case: a real personal token presented to the registry must not authenticate.
        var personal = ValidateTokenRequest.TokenPrefix + "abc123";
        Assert.Null(InstanceKeys.ExtractKey($"Bearer {personal}"));
    }

    [Fact]
    public void Hash_IsStable_LowercaseHex_AndDiffersPerKey()
    {
        var key = InstanceKeys.Generate();
        var hash = InstanceKeys.Hash(key);
        Assert.Equal(64, hash.Length);                       // SHA-256 as hex
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.Equal(hash, InstanceKeys.Hash(key));          // stable
        Assert.NotEqual(hash, InstanceKeys.Hash(InstanceKeys.Generate()));
    }

    [Theory]
    [InlineData("Bearer ")]
    [InlineData("Basic mwi_abc")]                            // wrong scheme
    [InlineData("mwi_abc")]                                  // no scheme
    [InlineData("Bearer notaninstancekey")]                  // no instance prefix
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MalformedHeaders_YieldNoKey(string? header)
        => Assert.Null(InstanceKeys.ExtractKey(header));

    [Theory]
    [InlineData("Bearer mwi_abc")]
    [InlineData("bearer mwi_abc")]                           // scheme is case-insensitive
    [InlineData("  Bearer   mwi_abc  ")]                     // stray whitespace is tolerated
    public void WellFormedHeaders_YieldTheKey(string header)
        => Assert.Equal("mwi_abc", InstanceKeys.ExtractKey(header));

    [Fact]
    public void HashEquals_MatchesOnlyIdenticalHashes()
    {
        var a = InstanceKeys.Hash(InstanceKeys.Generate());
        var b = InstanceKeys.Hash(InstanceKeys.Generate());
        Assert.True(InstanceKeys.HashEquals(a, a));
        Assert.False(InstanceKeys.HashEquals(a, b));
        // Length mismatch must fail rather than throw — a truncated/garbage stored hash is data,
        // not a crash.
        Assert.False(InstanceKeys.HashEquals(a, a[..10]));
        Assert.False(InstanceKeys.HashEquals("", a));
    }

    [Fact]
    public void HashPrefix_IsTheIndexNodeId()
    {
        var hash = InstanceKeys.Hash(InstanceKeys.Generate());
        Assert.Equal(hash[..InstanceKeys.HashPrefixLength], InstanceKeys.HashPrefix(hash));
    }

    [Fact]
    public void AuthorizationHeader_RoundTrips()
    {
        var key = InstanceKeys.Generate();
        Assert.Equal(key, InstanceKeys.ExtractKey(InstanceKeys.AuthorizationHeader(key)));
    }
}
