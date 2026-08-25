using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.Social.Test;

/// <summary>
/// The scope set <c>/connect/linkedin</c> asks LinkedIn for (issue #51).
///
/// <para>🚨 <b>This is a gate on whether ANYONE can connect at all.</b> LinkedIn rejects the whole
/// authorization — before any sign-in or consent screen — when the app is not approved for a
/// requested product: the member gets "Bummer, something went wrong" and a bounce back, with no
/// error the flow can read. Requesting <c>r_member_postAnalytics</c> unconditionally therefore
/// blocked every NEW connection on memex, while credentials connected before it was added kept
/// working — which is what made it look like an account problem rather than a scope problem.
/// Publishing never needed that scope.</para>
/// </summary>
public class LinkedInConnectScopeTest
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Fact]
    public void Default_RequestsOnlyWhatPublishingNeeds()
    {
        var scope = LinkedInConnectEndpoints.BuildScope(requestPostAnalytics: false);

        Assert.DoesNotContain(LinkedInConnectEndpoints.PostAnalyticsScope, scope, StringComparison.Ordinal);
        foreach (var required in new[] { "openid", "profile", "email", "w_member_social" })
            Assert.Contains(required, scope, StringComparison.Ordinal);
    }

    /// <summary>An unconfigured deployment — the overwhelming majority — must not ask for it.</summary>
    [Fact]
    public void UnconfiguredDeployment_DoesNotAskForAnalytics()
    {
        Assert.False(LinkedInConnectEndpoints.WantsPostAnalytics(Config()));
        Assert.False(LinkedInConnectEndpoints.WantsPostAnalytics(
            Config((LinkedInConnectEndpoints.RequestPostAnalyticsConfigKey, ""))));
        Assert.False(LinkedInConnectEndpoints.WantsPostAnalytics(
            Config((LinkedInConnectEndpoints.RequestPostAnalyticsConfigKey, "nonsense"))));
    }

    /// <summary>A deployment whose LinkedIn app DOES carry the product can still have it.</summary>
    [Fact]
    public void ApprovedDeployment_CanOptIn()
    {
        Assert.True(LinkedInConnectEndpoints.WantsPostAnalytics(
            Config((LinkedInConnectEndpoints.RequestPostAnalyticsConfigKey, "true"))));

        var scope = LinkedInConnectEndpoints.BuildScope(requestPostAnalytics: true);
        Assert.Contains(LinkedInConnectEndpoints.PostAnalyticsScope, scope, StringComparison.Ordinal);
        Assert.Contains("w_member_social", scope, StringComparison.Ordinal);
    }

    /// <summary>
    /// The config key is the one the options section actually binds from — a key under a different
    /// section would be silently never read, which is the shape of every "the flag does nothing" bug.
    /// </summary>
    [Fact]
    public void ConfigKey_LivesUnderTheLinkedInOptionsSection()
        => Assert.StartsWith(
            LinkedInOptions.SectionName + ":",
            LinkedInConnectEndpoints.RequestPostAnalyticsConfigKey,
            StringComparison.Ordinal);

    /// <summary>
    /// Degraded but HONEST: the callback can tell whether the analytics permission was actually
    /// granted, so a credential without it is reported rather than surfacing months later as
    /// impressions that are permanently zero. LinkedIn returns the granted scopes comma-separated.
    /// </summary>
    [Fact]
    public void GrantedScope_IsReadBackFromTheTokenResponse()
    {
        Assert.False(LinkedInConnectEndpoints.GrantsPostAnalytics("email,openid,profile,w_member_social"));
        Assert.False(LinkedInConnectEndpoints.GrantsPostAnalytics(null));
        Assert.True(LinkedInConnectEndpoints.GrantsPostAnalytics(
            "email,openid,profile,w_member_social,r_member_postAnalytics"));
    }
}
