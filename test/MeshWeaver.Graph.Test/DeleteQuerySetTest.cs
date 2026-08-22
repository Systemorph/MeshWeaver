using System;
using System.Linq;
using MeshWeaver.Data;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The PURE half of the query-set Delete URL (<c>/{anchor}/Delete?q=…</c>) — the clear URL an agent
/// hands the user when its own delete was refused, naming a whole SET of nodes via mesh queries.
///
/// <para>Pinned here: the URL builder round-trips through the REAL
/// <see cref="LayoutAreaReference"/> parameter parser (the same code path
/// <c>ApplicationPage</c> feeds by appending the browser's query string onto the reference
/// <c>Id</c>), including queries containing <c>=</c>, <c>&amp;</c> and newlines — the characters
/// that would break an unescaped parameter apart; the descendant pruning that keeps a recursive
/// parent delete from turning its already-covered children into phantom "not found" failures; and
/// the anchor-coverage rule that decides whether the page must redirect off its own deleted node.
/// The wiring (queries resolved, typed DELETE confirm, sequential server-authoritative deletes) is
/// exercised on the layout area itself.</para>
/// </summary>
public class DeleteQuerySetTest
{
    // ---- ParseQueries -------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n \n  ")]
    public void ParseQueries_NothingUsable_YieldsEmpty(string? raw)
        => DeleteLayoutArea.ParseQueries(raw).Should().BeEmpty();

    [Fact]
    public void ParseQueries_SplitsOnNewlines_TrimsAndDedupes()
    {
        var parsed = DeleteLayoutArea.ParseQueries(
            "nodeType:Foo scope:subtree\r\n  path:ACME/Old  \n\nnodeType:Foo scope:subtree");

        // \r\n is handled by TrimEntries, blank segments are dropped, an exact duplicate collapses.
        parsed.Should().Equal("nodeType:Foo scope:subtree", "path:ACME/Old");
    }

    // ---- BuildQueryDeleteUrl ------------------------------------------------------------

    [Fact]
    public void BuildQueryDeleteUrl_ShapesTheClearUrl()
    {
        var url = DeleteLayoutArea.BuildQueryDeleteUrl("ACME", ["path:ACME/Old scope:subtree"]);

        url.Should().StartWith("/ACME/Delete?q=");
        url.Should().NotContain(" ", "the query must be URL-escaped");
    }

    /// <summary>
    /// The round-trip that makes the URL REAL: <c>ApplicationPage</c> appends the browser's query
    /// string (leading <c>?</c> included) onto the area reference's <c>Id</c>, and the layout area
    /// reads the parameter back through <see cref="LayoutAreaReference.GetParameterValue"/>. That
    /// parser splits parts on <c>&amp;</c> and <c>=</c> BEFORE unescaping, so a query containing
    /// either character survives only when the builder escaped it — which is exactly what this
    /// pins, alongside the newline separator between multiple queries.
    /// </summary>
    [Fact]
    public void BuildQueryDeleteUrl_RoundTrips_ThroughTheRealParameterParser()
    {
        string[] queries =
        [
            "nodeType:Foo scope:subtree sort:Name-asc",
            "path:ACME/Old x=y&z", // '=' and '&' — the characters the parser splits on
        ];
        var url = DeleteLayoutArea.BuildQueryDeleteUrl("ACME/Sub", queries);
        url.Should().StartWith("/ACME/Sub/Delete?");

        // Exactly what ApplicationPage does with the URL's query string: id = id + "?…".
        var reference = new LayoutAreaReference("Delete") { Id = url[url.IndexOf('?')..] };

        var raw = reference.GetParameterValue(DeleteLayoutArea.QueriesParam);
        DeleteLayoutArea.ParseQueries(raw).Should().Equal(queries);
    }

    // ---- PruneRedundantDescendants ------------------------------------------------------

    [Fact]
    public void Prune_DropsPathsAlreadyCoveredByAnAncestor()
    {
        var pruned = DeleteLayoutArea.PruneRedundantDescendants(
            ["ACME/Old/Child", "ACME/Old", "ACME/Other", "ACME/Old/Child/Grand"]);

        // Deleting ACME/Old is recursive; its descendants beside it would only fail "not found".
        pruned.Should().Equal("ACME/Old", "ACME/Other");
    }

    [Fact]
    public void Prune_IsCaseInsensitive_AndDedupes()
    {
        var pruned = DeleteLayoutArea.PruneRedundantDescendants(
            ["acme/old", "ACME/Old", "ACME/OLD/Child"]);

        pruned.Should().HaveCount(1);
    }

    [Fact]
    public void Prune_SegmentBoundary_ASiblingWithACommonPrefixIsNotADescendant()
    {
        // "ACME/Ab" does NOT cover "ACME/Abc" — prefix alone is not ancestry.
        var pruned = DeleteLayoutArea.PruneRedundantDescendants(["ACME/Ab", "ACME/Abc"]);

        pruned.Should().Equal("ACME/Ab", "ACME/Abc");
    }

    [Fact]
    public void Prune_IgnoresEmptyEntries()
        => DeleteLayoutArea.PruneRedundantDescendants(["", "ACME/Old"]).Should().Equal("ACME/Old");

    // ---- CoversPath (the redirect-off-the-dead-anchor decision) -------------------------

    [Theory]
    [InlineData("ACME/Sub", true)]        // the anchor itself was deleted
    [InlineData("ACME", true)]            // an ancestor was deleted — the anchor went with it
    [InlineData("ACME/Su", false)]        // common prefix, different segment — anchor survives
    [InlineData("ACME/Sub/Child", false)] // a descendant of the anchor — anchor survives
    public void CoversPath_DecidesByAncestryNotPrefix(string deleted, bool covered)
        => DeleteLayoutArea.CoversPath([deleted], "ACME/Sub").Should().Be(covered);

    [Fact]
    public void CoversPath_EmptySet_CoversNothing()
        => DeleteLayoutArea.CoversPath(Array.Empty<string>(), "ACME/Sub").Should().BeFalse();
}
