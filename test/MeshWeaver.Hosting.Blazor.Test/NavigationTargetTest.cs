using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Pure parse tests for <see cref="NavigationTarget"/> — the split of a relative
/// navigation URL into its node-address ROUTE and its query-string ARGS. This is the
/// fix for the "/search?q=nodeType%3AThread&amp;groupBy=Namespace → lacks Thread
/// permission" defect: the query must never be glued onto the resolved node address.
/// </summary>
public class NavigationTargetTest
{
    [Fact]
    public void SearchPageWithQuery_SplitsRouteAndArgs()
    {
        // The exact URL the AI top-bar "Threads" item navigates to (query URL-encoded
        // as the browser reports it: %3A for ':').
        var target = NavigationTarget.Parse("/search?q=nodeType%3AThread&groupBy=Namespace");

        target.Path.Should().Be("search");
        target.Args.Should().HaveCount(2);
        target.Args["q"].Should().Be("nodeType:Thread");
        target.Args["groupBy"].Should().Be("Namespace");
    }

    [Fact]
    public void RealNodePath_NoQuery_RouteIsWholePath_ArgsEmpty()
    {
        var target = NavigationTarget.Parse("/AgenticPension/Jahresrechnung");

        target.Path.Should().Be("AgenticPension/Jahresrechnung");
        target.Args.Should().BeEmpty();
    }

    [Fact]
    public void NodeAreaPath_WithQuery_RouteExcludesQuery()
    {
        // A node/area URL that legitimately carries a query parameter — the route is the
        // node+area path; the query is an arg, never part of the address.
        var target = NavigationTarget.Parse("/AgenticPension/Overview?tab=summary&edit=true");

        target.Path.Should().Be("AgenticPension/Overview");
        target.Args["tab"].Should().Be("summary");
        target.Args["edit"].Should().Be("true");
    }

    [Fact]
    public void DeepPath_WithSpacesAndMultipleSegments_RoutePreserved()
    {
        // Segments containing spaces must survive intact (the encoded space comes as %20).
        var target = NavigationTarget.Parse("/AgenticPension/Data Analytics/Cessions/Overview");

        target.Path.Should().Be("AgenticPension/Data Analytics/Cessions/Overview");
        target.Args.Should().BeEmpty();
    }

    [Fact]
    public void NoLeadingSlash_StillParses()
    {
        var target = NavigationTarget.Parse("search?q=hello");

        target.Path.Should().Be("search");
        target.Args["q"].Should().Be("hello");
    }

    [Fact]
    public void Fragment_IsDiscarded()
    {
        var target = NavigationTarget.Parse("/Doc/GUI/DataBinding?mode=flat#golden-rule");

        target.Path.Should().Be("Doc/GUI/DataBinding");
        target.Args["mode"].Should().Be("flat");
        target.Args.Should().NotContainKey("golden-rule");
    }

    [Fact]
    public void EmptyValueAndFlagOnlyParams_ParseToEmptyString()
    {
        var target = NavigationTarget.Parse("/search?q=&flag&groupBy=NodeType");

        target.Args["q"].Should().Be("");
        target.Args["flag"].Should().Be("");
        target.Args["groupBy"].Should().Be("NodeType");
    }

    [Fact]
    public void PlusAndPercentEncoding_AreDecoded()
    {
        // '+' is the legacy space encoding in query strings; %2F is an encoded slash.
        var target = NavigationTarget.Parse("/search?q=hello+world&path=a%2Fb");

        target.Args["q"].Should().Be("hello world");
        target.Args["path"].Should().Be("a/b");
    }

    [Fact]
    public void DuplicateKey_LastValueWins()
    {
        var target = NavigationTarget.Parse("/search?hq=a&hq=b");

        target.Args["hq"].Should().Be("b");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmpty_YieldsEmptyRouteAndArgs(string? input)
    {
        var target = NavigationTarget.Parse(input);

        target.Path.Should().Be("");
        target.Args.Should().BeEmpty();
    }

    [Fact]
    public void Root_ParsesToEmptyRoute()
    {
        var target = NavigationTarget.Parse("/");

        target.Path.Should().Be("");
        target.Args.Should().BeEmpty();
    }

    [Fact]
    public void QueryWithEncodedAmpersandInValue_KeepsValueIntact()
    {
        // An encoded '&' (%26) inside a value must not be treated as a pair separator.
        var target = NavigationTarget.Parse("/search?q=a%26b&groupBy=Namespace");

        target.Args["q"].Should().Be("a&b");
        target.Args["groupBy"].Should().Be("Namespace");
    }
}
