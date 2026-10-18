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
        // The ONE exception is core's own approvals ledger: records live beside their document in any
        // partition, so those legs are keyed to the viewer and declared mesh-wide instead — and
        // IsLedgerQuery admits exactly those shapes (a contributed lookalike is refused, see
        // InboxApprovalsTest.AContributedCrossPartitionLeg_IsRefused).
        foreach (var leg in resolved.Where(l => InboxQueries.LedgerKindOf(l.Query) is null))
        {
            Assert.True(Anchored.IsMatch(leg.Query),
                $"leg '{leg.Source}/{leg.Band}' is not anchored to one partition: {leg.Query}");
            Assert.StartsWith($"namespace:{Viewer}/", leg.Query, StringComparison.Ordinal);
        }
        foreach (var leg in resolved.Where(l => InboxQueries.LedgerKindOf(l.Query) is not null))
            Assert.True(InboxQueries.IsLedgerQuery(leg.Query, Viewer), $"ledger leg '{leg.Band}' is not keyed to the viewer: {leg.Query}");
    }

    /// <summary>
    /// 🚨 An alternation classifies as "anchored" to the eye but takes the fan-out route, where the
    /// narrowing INTERSECTS with <c>searchable_schemas</c> — which excludes <c>Admin</c>, so a
    /// platform lane written that way vanishes with nothing red. Two legs, never one alternation.
    /// </summary>
    [Fact]
    public void NoLeg_UsesANamespaceAlternation()
    {
        // The ANCHOR terms (namespace:/path:) must be single-valued; a value alternation on a
        // content field (Recent's `content.status:Succeeded|…`) filters inside the one partition and
        // is not the fan-out this guards against.
        foreach (var leg in InboxQueries.Resolve(InboxQueries.BuiltIn, Viewer))
            foreach (var term in leg.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Where(t => t.StartsWith("namespace:", StringComparison.Ordinal)
                                     || t.StartsWith("path:", StringComparison.Ordinal)))
                Assert.DoesNotContain("|", term, StringComparison.Ordinal);
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

        Assert.False(Anchored.IsMatch(rogue.Query),
            "the anchor check must be able to FAIL, or it proves nothing about the built-ins");
        Assert.Empty(InboxQueries.Resolve([rogue], Viewer));
        Assert.Equal("Rogue", Assert.Single(InboxQueries.Refused([rogue], Viewer)).Source);
    }

    /// <summary>
    /// The runtime guard, not only the test's regex: a contributed leg is RUN only when it reads
    /// exactly the viewer's own partition. Another person's partition, a wildcard, an alternation and
    /// a declared fan-out are all refused; the viewer's own partition is not.
    /// </summary>
    [Theory]
    [InlineData("namespace:{viewer}/_Thread nodeType:Thread", true)]
    [InlineData("path:{viewer}/_Email nodeType:Email", true)]
    [InlineData("namespace:someone-else/_Thread nodeType:Thread", false)]
    [InlineData("namespace:*/_Thread nodeType:Thread", false)]
    [InlineData("namespace:{viewer}|someone-else nodeType:Thread", false)]
    [InlineData("namespace:{viewer}/_Thread partitions:all nodeType:Thread", false)]
    [InlineData("nodeType:Thread namespace:{viewer}/_Thread", false)]
    [InlineData("namespace:{viewer}x/_Thread", false)]
    [InlineData("namespace:{viewer}/_Thread namespace:someone-else/_Thread", false)]
    [InlineData("namespace:{viewer}/_Thread path:someone-else/_Thread", false)]
    [InlineData("path:{viewer}/_Email nodeType:Email path:someone-else/_Email", false)]
    public void OnlyALegReadingTheViewersOwnPartition_IsRun(string query, bool run)
        => AssertRun(query, run, Viewer);

    /// <summary>
    /// A partition key may be any Unicode letters and digits — a guard that only knew ASCII would
    /// refuse every leg of such a viewer and leave their inbox empty.
    /// </summary>
    [Theory]
    [InlineData("müller")]
    [InlineData("zoë-ångström")]
    public void AUnicodeViewer_GetsEveryBuiltInLeg(string viewer)
    {
        Assert.Equal(InboxQueries.BuiltIn.Length, InboxQueries.Resolve(InboxQueries.BuiltIn, viewer).Length);
        AssertRun("namespace:{viewer}/_Thread nodeType:Thread", true, viewer);
        AssertRun("namespace:someone-else/_Thread nodeType:Thread", false, viewer);
    }

    private static void AssertRun(string query, bool run, string viewer)
    {
        var leg = new InboxQueryLeg { Source = "Contributed", Band = InboxBand.NeedsYou, Query = query };
        Assert.Equal(run, InboxQueries.Resolve([leg], viewer).Length == 1);
        Assert.Equal(!run, InboxQueries.Refused([leg], viewer).Length == 1);
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
