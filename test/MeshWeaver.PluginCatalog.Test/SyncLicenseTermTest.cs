using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the TERM half of a sync licence — the part <see cref="PluginGrantTest"/> deliberately does
/// not cover, because until licences existed a grant could only be present or absent.
///
/// <para>Every instant here is explicit. An expiry that can only be exercised by waiting is an
/// expiry nobody pins, which is why <see cref="PluginGrant.Allows(string,string,DateTimeOffset)"/>
/// takes <c>now</c> as an argument rather than reading the ambient clock.</para>
/// </summary>
public class SyncLicenseTermTest
{
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static PluginGrant GrantWith(params PluginGrantEntry[] entries) =>
        new() { InstanceId = "manufacturing-ci", Entries = entries };

    private static PluginGrantEntry Entry(string source, string package, DateTimeOffset? expires = null) =>
        new() { Source = source, PackageId = package, ExpiresAt = expires };

    [Fact]
    public void NoExpiry_IsPerpetual()
    {
        // The compatibility guarantee: every grant written before licences existed has no
        // ExpiresAt, and must keep working exactly as it did.
        var grant = GrantWith(Entry("Plugins", "Publish"));
        Assert.True(grant.Allows("Plugins", "Publish", Now));
        Assert.True(grant.Allows("Plugins", "Publish", Now.AddYears(50)));
    }

    [Fact]
    public void WithinTerm_Allows()
    {
        var grant = GrantWith(Entry("Plugins", "Publish", Now.AddDays(30)));
        Assert.True(grant.Allows("Plugins", "Publish", Now));
    }

    [Fact]
    public void PastTerm_Denies()
    {
        var grant = GrantWith(Entry("Plugins", "Publish", Now.AddDays(-1)));
        Assert.False(grant.Allows("Plugins", "Publish", Now));
    }

    [Fact]
    public void TheExpiryInstantItself_IsAlreadyExpired()
    {
        // Half-open term: valid while now < ExpiresAt. Stating it as a test because "expires at T"
        // is read both ways in the wild, and a licence that outlives its stated end by a tick is
        // the kind of thing nobody notices until it matters.
        var grant = GrantWith(Entry("Plugins", "Publish", Now));
        Assert.False(grant.Allows("Plugins", "Publish", Now));
        Assert.True(grant.Allows("Plugins", "Publish", Now.AddTicks(-1)));
    }

    [Fact]
    public void ExpiryIsPerEntry_NotPerGrant()
    {
        // The reason the term lives on the entry: one instance routinely holds a perpetual licence
        // for the platform repo alongside a termed licence for a paid package.
        var grant = GrantWith(
            Entry("Plugins", PluginGrantEntry.AllPackages),
            Entry("Education", "DataModeling", Now.AddDays(-1)));

        Assert.True(grant.Allows("Plugins", "Store", Now));
        Assert.False(grant.Allows("Education", "DataModeling", Now));
    }

    [Fact]
    public void AWholeSourceGrant_ExpiresToo()
    {
        var grant = GrantWith(Entry("Education", PluginGrantEntry.AllPackages, Now.AddDays(-1)));
        Assert.False(grant.Allows("Education", "DataModeling", Now));
        Assert.True(grant.Allows("Education", "DataModeling", Now.AddDays(-2)));
    }

    [Fact]
    public void RevokedGrant_AuthorizesNothing_EvenWithLiveEntries()
    {
        var grant = GrantWith(Entry("Plugins", PluginGrantEntry.AllPackages)) with { IsRevoked = true };
        Assert.False(grant.Allows("Plugins", "Publish", Now));
        Assert.False(grant.Allows("Plugins", "Store", Now));
    }

    [Fact]
    public void RevocationKeepsTheRecord_SoItCanBeReviewedAndLifted()
    {
        // RevokeAll flips a flag rather than emptying Entries: what was licensed, under what terms,
        // has to survive the revocation or it cannot be reviewed afterwards — nor reinstated.
        var revoked = GrantWith(Entry("Plugins", "Publish", Now.AddDays(30))) with { IsRevoked = true };
        Assert.Single(revoked.Entries);
        Assert.False(revoked.Allows("Plugins", "Publish", Now));

        var reinstated = revoked with { IsRevoked = false };
        Assert.True(reinstated.Allows("Plugins", "Publish", Now));
    }

    [Fact]
    public void ReinstatingDoesNotRenew_AnExpiredEntryStaysExpired()
    {
        var revoked = GrantWith(Entry("Plugins", "Publish", Now.AddDays(-1))) with { IsRevoked = true };
        var reinstated = revoked with { IsRevoked = false };
        Assert.False(reinstated.Allows("Plugins", "Publish", Now));
    }

    [Fact]
    public void MatchesIgnoresTheTerm_SoExpiredIsDistinguishableFromUnlicensed()
    {
        // The two answers need different remedies — renew, versus buy — so the record has to be
        // able to tell them apart even though both deny.
        var expired = Entry("Plugins", "Publish", Now.AddDays(-1));
        Assert.True(expired.Matches("Plugins", "Publish"));
        Assert.False(expired.IsValidAt(Now));

        Assert.False(expired.Matches("Plugins", "Store"));
    }

    [Fact]
    public void TheTermsAreCarriedOnTheEntry()
    {
        // What makes this a licence rather than an ACL: the entry records what it was issued
        // under, and how it came about.
        var entry = new PluginGrantEntry
        {
            Source = "Plugins",
            PackageId = "Publish",
            ExpiresAt = Now.AddYears(1),
            IssuedUnderLicense = "Apache-2.0",
            IssuedVia = "order 4711",
            IssuedAt = Now,
        };

        Assert.Equal("Apache-2.0", entry.IssuedUnderLicense);
        Assert.Equal("order 4711", entry.IssuedVia);
        Assert.Equal(Now, entry.IssuedAt);
        Assert.Equal("Plugins/Publish", entry.ToString());
    }

    [Fact]
    public void UnspecifiedTermsStayNull_NeverDefaulted()
    {
        // Recording terms nobody granted is worse than recording none — the same rule
        // Package.License follows.
        var entry = Entry("Plugins", "Publish");
        Assert.Null(entry.IssuedUnderLicense);
        Assert.Null(entry.IssuedVia);
        Assert.Null(entry.ExpiresAt);
    }
}
