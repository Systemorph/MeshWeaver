using System.Text.Json;
using MeshWeaver.Social;
using Xunit;

namespace MeshWeaver.Social.Test;

/// <summary>
/// The pure parts of the member-post analytics call: LinkedIn's typed <c>entity</c> parameter and
/// the response summing. Both are fiddly and both fail silently in production if wrong — a bad
/// entity parameter 400s, and a mis-parsed response reports 0 views forever.
/// </summary>
public class LinkedInAnalyticsTest
{
    [Fact]
    public void EntityParameter_WrapsUgcPostUrn()
    {
        LinkedInAnalytics.EntityParameter("urn:li:ugcPost:6443156446455693312")
            .Should().Be("(ugc:urn%3Ali%3AugcPost%3A6443156446455693312)");
    }

    [Fact]
    public void EntityParameter_WrapsShareUrn()
    {
        LinkedInAnalytics.EntityParameter("urn:li:share:7325786486870552578")
            .Should().Be("(share:urn%3Ali%3Ashare%3A7325786486870552578)");
    }

    [Theory]
    [InlineData("urn:li:activity:123")]
    [InlineData("7325786486870552578")]
    [InlineData("")]
    [InlineData(null)]
    public void EntityParameter_NullForUnsupportedShapes(string? urn)
    {
        // Better to skip the call than fire a request LinkedIn will reject.
        LinkedInAnalytics.EntityParameter(urn).Should().BeNull();
    }

    [Fact]
    public void SumCounts_TotalAggregation_SingleElement()
    {
        var json = JsonDocument.Parse("""
            {"elements":[{"count":1234,"metricType":"IMPRESSION"}],"paging":{"count":10,"start":0}}
            """);
        LinkedInAnalytics.SumCounts(json.RootElement).Should().Be(1234);
    }

    [Fact]
    public void SumCounts_DailyAggregation_SumsEveryDay()
    {
        var json = JsonDocument.Parse("""
            {"elements":[{"count":10},{"count":20},{"count":5}]}
            """);
        LinkedInAnalytics.SumCounts(json.RootElement).Should().Be(35);
    }

    [Fact]
    public void SumCounts_AcceptsStringCounts()
    {
        // LinkedIn has shipped counts as both numbers and strings across versions.
        var json = JsonDocument.Parse("""{"elements":[{"count":"42"}]}""");
        LinkedInAnalytics.SumCounts(json.RootElement).Should().Be(42);
    }

    [Theory]
    [InlineData("""{"elements":[]}""")]
    [InlineData("""{"paging":{"count":0}}""")]
    [InlineData("""{"elements":{"count":5}}""")]
    [InlineData("""{"elements":[{"noCount":1}]}""")]
    [InlineData("""[]""")]
    public void SumCounts_MalformedPayloadsYieldZero(string payload)
    {
        // A stats refresh must never throw: a shape we don't recognise reports 0, not an exception.
        var json = JsonDocument.Parse(payload);
        LinkedInAnalytics.SumCounts(json.RootElement).Should().Be(0);
    }
}
