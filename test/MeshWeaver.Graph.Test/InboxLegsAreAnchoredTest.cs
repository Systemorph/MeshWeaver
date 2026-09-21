using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The one invariant the inbox cannot be allowed to lose: <b>every leg names exactly one
/// partition</b>.
///
/// <para>This is not a style rule. A leg with no concrete <c>namespace:</c>/<c>path:</c> anchor
/// becomes a <c>UNION ALL</c> over every partition schema — the shape measured seizing both
/// production portals, and measured on the notification bell at 4 476 rows across 201 of 201
/// schemas, 9–10 s per render, filtered to 0 rows in memory on an idle replica. Because providers
/// contribute legs as data, the next unanchored one will arrive from a package rather than from
/// this repository, so the check is on the RESOLVER's output and not on a hand-written list.</para>
/// </summary>
public class InboxLegsAreAnchoredTest
{
    private const string Viewer = "acme";

    /// <summary>A resolved leg must carry a concrete namespace anchor as its FIRST term.</summary>
    private static readonly Regex Anchored =
        new(@"^(namespace|path):[A-Za-z0-9_.\-]+", RegexOptions.Compiled);

    [Fact]
    public void EveryBuiltInLeg_ResolvesToExactlyOnePartition()
    {
        var resolved = InboxQueries.Resolve(InboxQueries.BuiltIn, Viewer);

        Assert.NotEmpty(resolved);
        foreach (var leg in resolved)
        {
            Assert.True(Anchored.IsMatch(leg.Query),
                $"leg '{leg.Source}/{leg.Band}' is not anchored to one partition: {leg.Query}");
            Assert.StartsWith($"namespace:{Viewer}/", leg.Query, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 🚨 An alternation classifies as "anchored" to the eye but takes the fan-out route, where the
    /// narrowing INTERSECTS with <c>searchable_schemas</c> — which excludes <c>Admin</c>, so a
    /// platform lane written that way vanishes with nothing red. Two legs, never one alternation.
    /// </summary>
    [Fact]
    public void NoLeg_UsesANamespaceAlternation()
    {
        foreach (var leg in InboxQueries.Resolve(InboxQueries.BuiltIn, Viewer))
            Assert.DoesNotContain("|", leg.Query, StringComparison.Ordinal);
    }

    /// <summary>The token must be fully substituted — a surviving one would query a literal path.</summary>
    [Fact]
    public void ResolvedLegs_CarryNoViewerToken()
    {
        foreach (var leg in InboxQueries.Resolve(InboxQueries.BuiltIn, Viewer))
            Assert.DoesNotContain(InboxQueries.ViewerToken, leg.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A blank viewer must FAIL, not resolve. <c>namespace:/_Notification</c> names no partition, so
    /// it is precisely the fan-out this shape exists to prevent — and it would look like it worked.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankViewer_IsRefused(string? viewer)
        => Assert.Throws<ArgumentException>(() => InboxQueries.Resolve(InboxQueries.BuiltIn, viewer!));

    /// <summary>
    /// The guard must hold for legs this repository never wrote — that is the whole point of a
    /// contributed-query design, and the negative control proving the test can fail.
    /// </summary>
    [Fact]
    public void AnUnanchoredContributedLeg_IsCaughtByTheSameCheck()
    {
        var rogue = new InboxQueryLeg
        {
            Source = "Rogue",
            Band = InboxBand.NeedsYou,
            Query = "nodeType:Notification sort:CreatedAt-desc",
        };

        var resolved = InboxQueries.Resolve([rogue], Viewer);

        Assert.False(Anchored.IsMatch(resolved.Single().Query),
            "the anchor check must be able to FAIL, or it proves nothing about the built-ins");
    }

    /// <summary>
    /// 🚨 Every leg must PROJECT. Without <c>select:</c> a leg returns whole nodes, content
    /// included — every document body and activity log fetched to render a one-line row, on every
    /// render, for every viewer, on a surface that stays open all day. The cost is invisible in a
    /// test mesh and ruinous in a real one, which is why it is asserted rather than reviewed.
    /// </summary>
    [Fact]
    public void EveryBuiltInLeg_ProjectsItsColumns()
    {
        foreach (var leg in InboxQueries.Resolve(InboxQueries.BuiltIn, Viewer))
            Assert.Contains("select:", leg.Query, StringComparison.Ordinal);
    }

    /// <summary>Disabled legs are skipped, so a deployment can turn one off without a build.</summary>
    [Fact]
    public void DisabledLegs_AreSkipped()
    {
        var off = new InboxQueryLeg
        {
            Source = "Off",
            Band = InboxBand.NeedsYou,
            Query = $"namespace:{InboxQueries.ViewerToken}/_Notification nodeType:Notification",
            Enabled = false,
        };

        Assert.Empty(InboxQueries.Resolve([off], Viewer));
    }

    /// <summary>
    /// Ordering is explicit, never registration order — a provider's position must not depend on
    /// when its assembly happened to load.
    /// </summary>
    [Fact]
    public void LegsAreOrderedWithinABand_ByTheirDeclaredOrder()
    {
        InboxQueryLeg Leg(string source, int order) => new()
        {
            Source = source,
            Band = InboxBand.Running,
            Order = order,
            Query = $"namespace:{InboxQueries.ViewerToken}/_Activity nodeType:Activity",
        };

        var resolved = InboxQueries.Resolve([Leg("late", 90), Leg("early", 10)], Viewer);

        Assert.Equal(["early", "late"], resolved.Select(leg => leg.Source).ToArray());
    }
}
