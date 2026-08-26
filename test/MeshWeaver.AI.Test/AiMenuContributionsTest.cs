using MeshWeaver.AI.Portal;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the AI (✨) menu's NAVIGATION half — the seeded <see cref="UiContribution"/> nodes this
/// module contributes. Its complement lives in <c>Memex.Portal.Shared.Test.AiMenuNewThreadTest</c>,
/// which pins the ONE compiled imperative entry ("New thread") the portal keeps.
/// <para>
/// 🚨 The two halves are deliberately asserted SEPARATELY, and the seam is the Order value. The
/// portal's imperative entry is Order 0; every seed here is Order &gt; 0. That single fact is what
/// makes the split lossless: the aggregator dedupes on (Order, Label, Area), so two entries drawn
/// from disjoint Order ranges can never collide, and neither test needs to reference the other
/// side's assembly to prove it. Before the AI engine became a module both halves were asserted in
/// one portal-side test, which meant the portal's test project had a compile-time dependency on
/// MeshWeaver.AI — the very edge the module lane exists to remove.
/// </para>
/// </summary>
public class AiMenuContributionsTest
{
    [Fact]
    public void Every_Seed_Sorts_After_The_Imperative_Entry()
    {
        // Order > 0 is this side's half of the ordering contract: "New thread" holds Order 0 in the
        // portal, so anything seeded here sorts behind it no matter what else is contributed.
        Assert.NotEmpty(AiMenuContributions.Seeds);
        Assert.All(AiMenuContributions.Seeds, seed => Assert.True(
            Assert.IsType<UiContribution>(seed.Content).Order > 0,
            $"'{seed.Name}' must sort after the imperative 'New thread' entry (Order 0)"));
    }

    [Fact]
    public void Every_Seed_Navigates_By_Href_And_Localizes_Its_Label()
    {
        // The inverse of the portal's sentinel contract: everything contributed as DATA must be a
        // plain navigation, so it works identically from any page and needs no client state. A seed
        // without an Href would render as a menu entry that does nothing when clicked.
        Assert.All(AiMenuContributions.Seeds, seed =>
        {
            var contribution = Assert.IsType<UiContribution>(seed.Content);
            Assert.Equal(NodeMenuItemsExtensions.AiMenuContext, contribution.Context);
            Assert.False(string.IsNullOrEmpty(contribution.Href), $"'{seed.Name}' must navigate by Href");
            Assert.False(string.IsNullOrEmpty(contribution.LabelKey), $"'{seed.Name}' must localize its label");
        });
    }

    [Fact]
    public void Seeds_Are_Uniquely_Keyed()
    {
        // The aggregator dedupes on (Order, Label, Area) via ImmutableSortedSet — two entries
        // colliding on all three would silently drop one from the rendered menu.
        var keys = AiMenuContributions.Seeds
            .Select(seed =>
            {
                var c = Assert.IsType<UiContribution>(seed.Content);
                return (c.Order, c.Label, c.Area);
            })
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
