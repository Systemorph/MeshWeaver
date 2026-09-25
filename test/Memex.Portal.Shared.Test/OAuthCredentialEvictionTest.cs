using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Memex.Portal.Shared.Authentication;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The pure eviction rule behind the <c>/token</c> exchange (#5074): a client keeps its N newest live
/// credentials and a fresh authorization evicts only what falls below them.
///
/// <para><b>SHOULD-FAIL-IF</b> the rule reverts to one credential per client (every sibling of the new
/// token evicted), or evicts a token NEWER than the evaluating exchange (the concurrent-exchange case),
/// or lets a revoked row hold one of the kept slots.</para>
/// </summary>
public class OAuthCredentialEvictionTest
{
    private const string Label = "OAuth: shared-client";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ApiTokenInfo Token(int minute, string? label = null, bool revoked = false) => new()
    {
        NodePath = $"u/ApiToken/t{minute:D2}",
        Label = label ?? Label,
        CreatedAt = T0.AddMinutes(minute),
        ExpiresAt = T0.AddYears(1),
        IsRevoked = revoked,
    };

    [Fact]
    public void BelowTheBound_NothingIsEvicted()
    {
        // Four older credentials + the new one = five = the default bound.
        var tokens = Enumerable.Range(1, 5).Select(m => Token(m)).ToArray();

        OAuthCredentialEviction.Evict(tokens, Label, "u/ApiToken/t05", T0.AddMinutes(5),
                OAuthCredentialBound.Default)
            .Should().BeEmpty("five live credentials is within a bound of five — the sibling sessions keep theirs");
    }

    [Fact]
    public void AboveTheBound_TheOldestAreEvicted_OldestFirst()
    {
        var tokens = Enumerable.Range(1, 7).Select(m => Token(m)).ToArray();

        OAuthCredentialEviction.Evict(tokens, Label, "u/ApiToken/t07", T0.AddMinutes(7), 5)
            .Should().Equal(["u/ApiToken/t01", "u/ApiToken/t02"],
                "seven credentials under a bound of five evicts exactly the two oldest");
    }

    [Fact]
    public void ABoundOfOne_IsTheOldOnePerClientRule()
    {
        var tokens = Enumerable.Range(1, 3).Select(m => Token(m)).ToArray();

        OAuthCredentialEviction.Evict(tokens, Label, "u/ApiToken/t03", T0.AddMinutes(3), 1)
            .Should().Equal(["u/ApiToken/t01", "u/ApiToken/t02"]);
        OAuthCredentialEviction.Evict(tokens, Label, "u/ApiToken/t03", T0.AddMinutes(3), 0)
            .Should().Equal(["u/ApiToken/t01", "u/ApiToken/t02"], "a bound below one is treated as one");
    }

    [Fact]
    public void ANewerToken_IsNeverEvicted_EvenByAnExchangeThatSeesIt()
    {
        // A concurrent exchange minted t09 while t08's exchange was listing. t08's evaluation sees
        // t09 and must leave it alone — only strictly-older rows are candidates.
        var tokens = Enumerable.Range(1, 9).Select(m => Token(m)).ToArray();

        var evicted = OAuthCredentialEviction.Evict(tokens, Label, "u/ApiToken/t08", T0.AddMinutes(8), 2);

        evicted.Should().NotContain("u/ApiToken/t09");
        evicted.Should().Equal(Enumerable.Range(1, 6).Select(m => $"u/ApiToken/t{m:D2}"),
            "t08's exchange keeps itself and t07 (a bound of two) and evicts t01..t06");
    }

    [Fact]
    public void ConcurrentExchanges_Converge_OnTheNewestN()
    {
        // Every exchange evaluates against the full listing; the union of what they evict must leave
        // exactly the N newest, whatever order they run in.
        var tokens = Enumerable.Range(1, 8).Select(m => Token(m)).ToArray();
        const int bound = 3;

        var evictedByAll = tokens
            .SelectMany(t => OAuthCredentialEviction.Evict(tokens, Label, t.NodePath, t.CreatedAt, bound))
            .ToHashSet();

        tokens.Select(t => t.NodePath).Where(p => !evictedByAll.Contains(p))
            .Should().Equal(["u/ApiToken/t06", "u/ApiToken/t07", "u/ApiToken/t08"]);
    }

    [Fact]
    public void ALaggingListing_EvictsLess_NeverANewerToken()
    {
        // The listing trails the store: t03 and t04 are not in it yet. The exchange for t05 may then
        // keep an older row it would otherwise evict, but must never evict anything newer than it.
        var listing = new[] { Token(1), Token(2), Token(5) };

        OAuthCredentialEviction.Evict(listing, Label, "u/ApiToken/t05", T0.AddMinutes(5), 2)
            .Should().Equal(["u/ApiToken/t01"], "only the older rows it SEES beyond the one kept slot");
    }

    [Fact]
    public void ARevokedRow_TakesNoKeptSlot_AndIsRemoved()
    {
        var tokens = new[] { Token(1), Token(2), Token(3, revoked: true), Token(4) };

        // A bound of two keeps ONE older credential beside t04. The newest older row, t03, is revoked,
        // so the slot goes to t02; t03 is cleaned up and t01 falls below the bound.
        OAuthCredentialEviction.Evict(tokens, Label, "u/ApiToken/t04", T0.AddMinutes(4), 2)
            .Should().Equal(["u/ApiToken/t01", "u/ApiToken/t03"],
                "a revoked row must not hold the kept slot — had it, t02 would have been evicted instead");
        OAuthCredentialEviction.Evict(tokens, Label, "u/ApiToken/t04", T0.AddMinutes(4), 3)
            .Should().Equal(["u/ApiToken/t03"], "below the bound only the dead row goes");
    }

    [Fact]
    public void ADeadRowNewerThanTheExchange_IsRemovedToo()
    {
        // A concurrent exchange minted t05 after t04's, and it was revoked since. It opens nothing, so
        // t04's exchange removes it even though it is newer; a newer LIVE row stays untouched.
        var tokens = new[] { Token(1), Token(4), Token(5, revoked: true), Token(6) };

        OAuthCredentialEviction.Evict(tokens, Label, "u/ApiToken/t04", T0.AddMinutes(4), 5)
            .Should().Equal(["u/ApiToken/t05"]);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData("3", 3)]
    [InlineData("0", 1)]
    [InlineData("-4", 1)]
    [InlineData("many", 5)]
    public void TheBound_ComesFromConfiguration_WithADefaultOfFive(string? configured, int expected)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(configured is null
                ? []
                : [new(OAuthCredentialBound.ConfigKey, configured)])
            .Build();

        OAuthCredentialBound.From(configuration).Should().Be(expected);
    }

    [Fact]
    public void NoConfigurationAtAll_IsTheDefault()
        // The shape of a caller that builds the controller's provider without IConfiguration.
        => OAuthCredentialBound.From(null).Should().Be(OAuthCredentialBound.Default);

    [Fact]
    public void AnotherClientsTokens_AreNeverTouched()
    {
        var tokens = new[] { Token(1, "OAuth: other"), Token(2, "OAuth: other"), Token(3) };

        OAuthCredentialEviction.Evict(tokens, Label, "u/ApiToken/t03", T0.AddMinutes(3), 1)
            .Should().BeEmpty("the label is the client identity; another client's rows are not candidates");
    }

    [Fact]
    public void ASameTickTie_IsBrokenByPath()
    {
        var a = Token(1) with { NodePath = "u/ApiToken/aaa" };
        var b = Token(1) with { NodePath = "u/ApiToken/bbb" };

        OAuthCredentialEviction.Evict([a, b], Label, b.NodePath, b.CreatedAt, 1)
            .Should().Equal(["u/ApiToken/aaa"]);
        OAuthCredentialEviction.Evict([a, b], Label, a.NodePath, a.CreatedAt, 1)
            .Should().BeEmpty("aaa sorts below bbb on the tie, so bbb is not older than aaa");
    }
}
