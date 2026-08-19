using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The compiled enforcement point of the data-contributed menu lane (#1645). These tests pin the
/// SECURITY properties the design rests on: a contribution can only ever NARROW its own
/// visibility — the permission gate is floored at Read and enforced against the viewer's
/// effective permission, an anonymous viewer (arriving as <see cref="Permission.None"/>) gets
/// nothing, and every node-shape gate in the closed vocabulary subtracts.
/// </summary>
public class UiContributionProjectionTest
{
    private static (MeshNode, UiContribution) Contribution(UiContribution content) =>
        (new MeshNode("c1", "Plugins/Contribs") { Name = "c1", NodeType = UiContributionNodeType.NodeType },
         content);

    private static readonly MeshNode SomeNode =
        new("Doc", "Org") { Name = "Doc", NodeType = "Markdown" };

    private static IReadOnlyCollection<NodeMenuItemDefinition> Project(
        UiContribution content,
        string context = UiContribution.NodeContext,
        MeshNode? node = null,
        Permission perms = Permission.Read,
        bool isAdmin = false)
        => UiContributionProjection.ProjectMenu(
            [Contribution(content)], context, "Org/Doc", node ?? SomeNode, perms, isAdmin);

    [Fact]
    public void AnonymousViewer_PermissionNone_GetsNothing_EvenWhenTheContributionDemandsNothing()
    {
        // The aggregator forces Permission.None for anonymous; the floor (Read) must filter.
        Assert.Empty(Project(new UiContribution { Area = "MyArea" }, perms: Permission.None));
    }

    [Fact]
    public void PermissionFloor_IsRead_AndReadViewerSeesTheEntry()
    {
        var item = Assert.Single(Project(new UiContribution { Area = "MyArea", Label = "Mine" }));
        Assert.Equal("Mine", item.Label);
        Assert.Equal(Permission.Read, item.RequiredPermission);
        Assert.Equal("MyArea", item.Area);
    }

    [Fact]
    public void DeclaredPermission_IsEnforced_AgainstTheViewersEffectivePermission()
    {
        var demandsUpdate = new UiContribution { Area = "MyArea", RequiredPermission = Permission.Update };
        Assert.Empty(Project(demandsUpdate, perms: Permission.Read));
        Assert.Single(Project(demandsUpdate, perms: Permission.Read | Permission.Update));
    }

    [Fact]
    public void AdminOnly_Subtracts_ForNonAdmins()
    {
        var adminEntry = new UiContribution
        {
            Area = "AdminArea",
            Gates = new UiContributionGates { AdminOnly = true },
        };
        Assert.Empty(Project(adminEntry, isAdmin: false));
        Assert.Single(Project(adminEntry, isAdmin: true));
    }

    [Fact]
    public void ExcludePartitionRoot_UsesTheSharedProtectedRootPredicate()
    {
        var userRoot = new MeshNode("alice", "") { Name = "alice", NodeType = "User" };
        var gated = new UiContribution
        {
            Area = "MyArea",
            Gates = new UiContributionGates { ExcludePartitionRoot = true },
        };
        Assert.Empty(Project(gated, node: userRoot));
        Assert.Single(Project(gated, node: SomeNode));
    }

    [Fact]
    public void NodeTypeGate_IsSuffixAware_LikeEveryPlatformMatches()
    {
        var slideOnly = new UiContribution
        {
            Area = "MyArea",
            Gates = new UiContributionGates { NodeTypes = ImmutableList.Create("Slide") },
        };
        var pluginSlide = new MeshNode("S1", "Org") { Name = "S1", NodeType = "Publish/Slide" };
        var bareSlide = new MeshNode("S2", "Org") { Name = "S2", NodeType = "Slide" };

        Assert.Single(Project(slideOnly, node: pluginSlide));
        Assert.Single(Project(slideOnly, node: bareSlide));
        Assert.Empty(Project(slideOnly, node: SomeNode));
    }

    [Fact]
    public void ContextRouting_IsExact_AndSettingsContributionsNeverLeakIntoTheNodeMenu()
    {
        var settingsTab = new UiContribution { Area = "TabArea", Context = UiContribution.SettingsContext };
        Assert.Empty(Project(settingsTab, context: UiContribution.NodeContext));

        var nodeEntry = new UiContribution { Area = "MyArea" };   // Context unset ⇒ Node
        Assert.Empty(Project(nodeEntry, context: UiContribution.MeshContext));
        Assert.Single(Project(nodeEntry, context: UiContribution.NodeContext));
    }

    [Fact]
    public void MissingArea_ContributesNothing()
    {
        Assert.Empty(Project(new UiContribution { Label = "No target" }));
    }

    [Fact]
    public void DeclaredHref_Overrides_TheDerivedAreaUrl_AndTooltipsCarryThrough()
    {
        var item = Assert.Single(Project(new UiContribution
        {
            Context = "AI",
            Area = "AiThreads",
            Href = "/search?q=nodeType%3AThread&groupBy=Namespace",
            Tooltip = "Conversation threads",
            TooltipKey = "menu.threadsTooltip",
        }, context: "AI"));
        Assert.Equal("/search?q=nodeType%3AThread&groupBy=Namespace", item.Href);
        Assert.Equal("Conversation threads", item.Tooltip);
        Assert.Equal("menu.threadsTooltip", item.TooltipKey);

        // Without a declared Href the entry opens its area on the anchoring node, as before.
        var derived = Assert.Single(Project(new UiContribution { Area = "MyArea" }));
        Assert.Contains("MyArea", derived.Href);
    }

    [Fact]
    public void NodeToken_InAHref_IsSubstitutedWithTheAnchoringNode_Escaped()
    {
        // The shape a node-native package needs: its OWN workspace area, told which node it is
        // being opened from. Nothing else can express this — an area name alone would resolve on
        // the anchoring node's hub, where a plugin's area does not exist.
        var item = Assert.Single(Project(new UiContribution
        {
            Area = "RequestApproval",
            Href = "/Approvals/Workspace/RequestApproval?doc={node}"
        }));
        Assert.Equal("/Approvals/Workspace/RequestApproval?doc=Org%2FDoc", item.Href);
    }

    [Fact]
    public void NodeToken_IsEscaped_SoAPathCanNeverIntroduceASchemeOrHost()
    {
        // The substituted value is a mesh path, escaped — the gate then judges the RESULT, so a
        // token cannot be used to smuggle a non-internal destination past the check.
        var item = Assert.Single(UiContributionProjection.ProjectMenu(
            [Contribution(new UiContribution { Area = "A", Href = "/desk?doc={node}" })],
            UiContribution.NodeContext, "Org/Doc?x=1&y=2", SomeNode, Permission.Read, false));
        Assert.Equal("/desk?doc=Org%2FDoc%3Fx%3D1%26y%3D2", item.Href);
    }

    [Fact]
    public void NonInternalHref_IsStillDiscarded_AfterSubstitution()
    {
        // The gate applies to the RESOLVED string, never to the template.
        var item = Assert.Single(Project(new UiContribution
        {
            Area = "MyArea",
            Href = "https://evil.example/{node}"
        }));
        Assert.Contains("MyArea", item.Href);
        Assert.DoesNotContain("evil.example", item.Href);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example/phish")]
    [InlineData("data:text/html,x")]
    public void NonInternalHref_IsDiscarded_AndTheEntryFallsBackToItsAreaUrl(string href)
    {
        // Href is mesh DATA reaching navigation — schemes and protocol-relative hosts must never
        // pass the compiled gate (XSS/phishing surface). The entry degrades to its area link.
        var item = Assert.Single(Project(new UiContribution { Area = "MyArea", Href = href }));
        Assert.DoesNotContain(href, item.Href);
        Assert.Contains("MyArea", item.Href);
    }

    [Fact]
    public void TopBarDeclaration_ProjectsAsAMenuButton_InTheTopBarContextOnly()
    {
        // A whole NEW top-bar menu is itself a contribution in the TopBar context: Area names the
        // menu's context key, Label/Icon/Order style the button, and the closed gate vocabulary
        // applies (an AdminOnly menu disappears wholesale for non-admins).
        var declaration = new UiContribution
        {
            Context = UiContribution.TopBarContext,
            Area = "Reinsurance",
            Label = "Reinsurance",
            Icon = "📊",
            Order = 60,
            Gates = new UiContributionGates { AdminOnly = true },
        };

        Assert.Empty(Project(declaration));                                     // not in the Node menu
        Assert.Empty(Project(declaration, context: UiContribution.TopBarContext)); // non-admin: hidden
        var button = Assert.Single(Project(declaration, context: UiContribution.TopBarContext, isAdmin: true));
        Assert.Equal("Reinsurance", button.Area);
        Assert.Equal(60, button.Order);
    }

    [Fact]
    public void SettingsTabs_GateOnAdminOnly_AndCarryTheContributedArea()
    {
        var contributions = new[]
        {
            Contribution(new UiContribution
            {
                Context = UiContribution.SettingsContext, Area = "TabArea", Label = "My Tab", Order = 7,
            }),
            Contribution(new UiContribution
            {
                Context = UiContribution.SettingsContext, Area = "AdminTab",
                Gates = new UiContributionGates { AdminOnly = true },
            }),
        };

        var forUser = UiContributionProjection.ProjectSettingsTabs(contributions, isAdmin: false);
        var tab = Assert.Single(forUser);
        Assert.Equal("My Tab", tab.Label);
        Assert.Equal(7, tab.Order);

        Assert.Equal(2, UiContributionProjection.ProjectSettingsTabs(contributions, isAdmin: true).Count);
    }

    [Fact]
    public void SettingsTabs_CarryGroupingAndStableId_AndResolveFluentIconNames()
    {
        var contributions = new[]
        {
            (new MeshNode("Privacy", "Admin/UiContribution")
                { Name = "Privacy", NodeType = UiContributionNodeType.NodeType },
             new UiContribution
             {
                 Context = UiContribution.SettingsContext,
                 Area = "SettingsPrivacy",
                 Label = "Privacy",
                 LabelKey = "settings.privacy",
                 Icon = "Shield",
                 Group = "Administration",
                 GroupKey = "settings.groupAdministration",
                 GroupIcon = "Shield",
                 Order = 330,
             }),
        };

        var tab = Assert.Single(UiContributionProjection.ProjectSettingsTabs(contributions, isAdmin: false));
        // The node's trailing path segment IS the tab id — the /GlobalSettings/{Id} deep link a
        // compiled tab had before migrating must survive the migration.
        Assert.Equal("Privacy", tab.Id);
        Assert.Equal("Administration", tab.Group);
        Assert.Equal("settings.groupAdministration", tab.GroupKey);
        Assert.Equal("settings.privacy", tab.LabelKey);
        // Icon strings go through the platform's total Icon.Parse — a Fluent name becomes a
        // fluent-provider Icon object (the NavMenu renderer's contract); both slots resolve alike.
        var icon = Assert.IsType<Domain.Icon>(tab.Icon);
        Assert.Equal(Domain.Icon.FluentProvider, icon.Provider);
        Assert.Equal("Shield", icon.Id);
        Assert.IsType<Domain.Icon>(tab.GroupIcon);
    }
}
