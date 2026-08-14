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
    // Basic IS an accepted scheme (NuGet clients cannot send Bearer), but its payload must be
    // base64 of "user:key" — a raw key after the scheme is not, and `_` is not a base64 character.
    [InlineData("Basic mwi_abc")]
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

    /// <summary>
    /// A NuGet client CANNOT send Bearer: <c>packageSourceCredentials</c> speaks Basic, and the
    /// only alternative is shipping a credential-provider plugin. Accepting Basic keeps ONE
    /// credential and ONE validator for both MeshWeaver's own clients and `dotnet restore`.
    ///
    /// <para>The username half is ignored — the key is the whole secret — so any username works,
    /// which matters because NuGet requires one to be present.</para>
    /// </summary>
    [Theory]
    [InlineData("instance")]
    [InlineData("anything")]
    [InlineData("")]
    public void BasicCredential_YieldsThePasswordHalf(string username)
    {
        var header = "Basic " + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{username}:mwi_abc"));

        Assert.Equal("mwi_abc", InstanceKeys.ExtractKey(header));
    }

    /// <summary>
    /// Malformed Basic payloads are an unauthenticated caller (→ 401), never an exception (→ 500).
    /// An endpoint that 500s on a bad header hands an unauthenticated caller a way to make the
    /// registry throw.
    /// </summary>
    [Theory]
    [InlineData("Basic !!!not-base64!!!")]
    [InlineData("Basic ")]
    public void MalformedBasicCredential_YieldsNoKeyAndDoesNotThrow(string header)
        => Assert.Null(InstanceKeys.ExtractKey(header));

    [Fact]
    public void BasicCredentialWithoutAColon_YieldsNoKey()
    {
        // No colon means no password half — there is nothing to read as a key, and reading the
        // whole blob would accept a username that merely looks like one.
        var header = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("mwi_abc"));

        Assert.Null(InstanceKeys.ExtractKey(header));
    }

    [Fact]
    public void OverlongBasicCredential_YieldsNoKeyWithoutDecoding()
    {
        // Unauthenticated, attacker-controlled input: the reject path must not throw and must not
        // decode an unbounded blob. A real credential is a username plus a 32-byte key.
        var header = "Basic " + new string('A', 5000);

        Assert.Null(InstanceKeys.ExtractKey(header));
    }

    [Fact]
    public void BasicCredentialWithAPersonalToken_YieldsNoKey()
    {
        // The prefix separation that keeps a USER credential from ever authenticating as an
        // instance must hold on the Basic path too, or the new scheme becomes a way around it.
        var header = "Basic " + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("user:mw_personaltoken"));

        Assert.Null(InstanceKeys.ExtractKey(header));
    }

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
