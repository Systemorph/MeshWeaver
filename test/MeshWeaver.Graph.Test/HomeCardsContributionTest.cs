using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The START-PAGE contribution lane (<see cref="UiContribution.HomeContext"/>): a package embeds ONE card on the
/// signed-in person's home through data, under the same rules as the inbox lane — only from its own partition, in
/// order, gated on the user node, never less than Read — and the default welcome template carries the region that
/// renders them, under the heading and above the composer.
/// </summary>
public class HomeCardsContributionTest
{
    private static (MeshNode, UiContribution) C(string path, UiContribution content) => (MeshNode.FromPath(path), content);

    private static readonly MeshNode UserHome = MeshNode.FromPath("jroe") with { NodeType = UserNodeType.NodeType };

    [Fact]
    public void AHomeContribution_IsEmbedded_OnlyFromItsOwnPartition_InOrder_WithAUniqueId()
    {
        var contributions = new[]
        {
            C("Edu/HomeCards/LearningPath", new UiContribution { Context = UiContribution.HomeContext, Area = "LearningPathNudge", Address = "Edu/Courses", Order = 20 }),
            C("Other/HomeCards/First", new UiContribution { Context = UiContribution.HomeContext, Area = "First", Order = 1 }),
            C("Other/HomeCards/Foreign", new UiContribution { Context = UiContribution.HomeContext, Area = "X", Address = "Edu/Courses" }),
            C("Other/HomeCards/Admin", new UiContribution { Context = UiContribution.HomeContext, Area = "Y", Gates = new UiContributionGates { AdminOnly = true } }),
            C("Edu/Inbox/Z", new UiContribution { Context = UiContribution.InboxContext, Area = "Z" }),
        };

        var areas = UiContributionProjection.ProjectHomeAreas(contributions, "jroe", UserHome, isAdmin: false, viewerId: "jroe");

        areas.Select(a => (a.Area, a.Address)).Should().Equal([("First", null), ("LearningPathNudge", "Edu/Courses")],
            "in order; an address outside the contributor's partition, an admin-only entry for a non-admin and another context are dropped");
        areas.Select(a => a.Id).Should().OnlyHaveUniqueItems("two packages' cards must never share a layout slot");
        UiContributionProjection.ProjectInboxAreas(contributions, "jroe", UserHome, isAdmin: false, viewerId: "jroe")
            .Select(a => a.Area).Should().Equal(["Z"], "the inbox lane still reads only its own context");
        UiContributionSeedValidation.PlatformContexts.Should().Contain(UiContribution.HomeContext, "a seed naming it is not 'declared by nobody'");
    }

    [Fact]
    public void AHomeContribution_NeedsTheViewersLivePermission_AndAShapeGateWaitsForTheNode()
    {
        var contributions = new[]
        {
            C("Edu/HomeCards/Read", new UiContribution { Context = UiContribution.HomeContext, Area = "R" }),
            C("Edu/HomeCards/Sync", new UiContribution { Context = UiContribution.HomeContext, Area = "S", RequiredPermission = Permission.Sync }),
            C("Edu/HomeCards/Shape", new UiContribution
            {
                Context = UiContribution.HomeContext, Area = "H", Gates = new UiContributionGates { ExcludePartitionRoot = true },
            }),
        };
        var projected = UiContributionProjection.ProjectHomeAreas(contributions, "jroe", UserHome, isAdmin: false, viewerId: "jroe");

        UiContributionProjection.FilterInboxAreas(projected, Permission.Read).Select(a => a.Area).Should().Equal(["R"],
            "an entry demanding Sync is hidden from a viewer without it, and the home is a partition root");
        UiContributionProjection.FilterInboxAreas(projected, Permission.None).Should().BeEmpty("never less than Read");
        UiContributionProjection.ProjectHomeAreas(contributions, "jroe", userNode: null, isAdmin: false, viewerId: "jroe")
            .Select(a => a.Area).Should().NotContain("H", "a gate that needs the node's shape narrows while the node is unknown");
    }

    [Fact]
    public void TheHomeCardsRegion_EmbedsEachCardOnItsOwnAddress_AndTheWelcomeTemplateCarriesIt()
    {
        var cards = UserActivityLayoutAreas.HomeCards(new Address("jroe"),
            [("a", "Edu/Courses", "LearningPathNudge", 1, Permission.Read), ("b", null, "Own", 2, Permission.Read)]);
        // The container keeps its child views protected; read them the way the layout renderer receives them.
        var views = (System.Collections.IEnumerable)typeof(StackControl).BaseType!
            .GetProperty("Views", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(cards)!;
        var embedded = views.OfType<LayoutAreaControl>().ToList();
        embedded.Select(e => (e.Address.ToString(), e.Reference.Area)).Should().Equal([("Edu/Courses", "LearningPathNudge"), ("jroe", "Own")],
            "each card renders on its contributor's address; one that names none renders on the user's own hub");
        UserActivityLayoutAreas.HomeCards(new Address("jroe"), []).Areas.Should().BeEmpty("nothing declared, nothing shown — no gap");

        var md = UserActivityLayoutAreas.UserWelcomeMarkdown("Roland");
        md.Should().Contain("@@(\"area/HomeCards\")");
        md.IndexOf("Welcome back", StringComparison.Ordinal).Should().BeLessThan(md.IndexOf("area/HomeCards", StringComparison.Ordinal),
            "the cards sit under the heading");
        md.IndexOf("area/HomeCards", StringComparison.Ordinal).Should().BeLessThan(md.IndexOf("area/Composer", StringComparison.Ordinal),
            "and above the composer: a timely prompt is the first thing the person reads");
    }
}
