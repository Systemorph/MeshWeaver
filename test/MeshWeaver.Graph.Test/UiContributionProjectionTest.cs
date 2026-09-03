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
        bool isAdmin = false,
        string menuPath = "Org/Doc",
        string? viewerId = null)
        => UiContributionProjection.ProjectMenu(
            [Contribution(content)], context, menuPath, node ?? SomeNode, perms, isAdmin, viewerId);

    private static IReadOnlyList<SettingsMenuItemDefinition> ProjectNodeSettings(
        UiContribution content,
        MeshNode? node = null,
        bool isAdmin = false,
        string menuPath = "Org/Doc",
        string? viewerId = null)
        => UiContributionProjection.ProjectNodeSettingsTabs(
            [Contribution(content)], menuPath, node ?? SomeNode, isAdmin, viewerId);

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

    [Fact]
    public void SyncedOnly_Subtracts_OnceTheNodeHasBeenClaimed()
    {
        // The gate the "Stop synchronization" entry needs: a node the viewer already claimed has
        // nothing left to stop, so the entry must not offer it.
        var gated = new UiContribution
        {
            Area = "StopSync",
            Gates = new UiContributionGates { SyncedOnly = true },
        };
        var synced = new MeshNode("Doc", "Org") { NodeType = "Markdown", SyncBehavior = SyncBehavior.Include };
        var claimed = new MeshNode("Doc", "Org") { NodeType = "Markdown", SyncBehavior = SyncBehavior.ExcludeThisOnly };
        var subtreeClaimed = new MeshNode("Doc", "Org")
            { NodeType = "Markdown", SyncBehavior = SyncBehavior.ExcludeThisAndChildren };

        Assert.Single(Project(gated, node: synced));
        Assert.Empty(Project(gated, node: claimed));
        Assert.Empty(Project(gated, node: subtreeClaimed));

        // Missing evidence NARROWS: an unresolved node cannot prove it is still synced.
        Assert.Empty(UiContributionProjection.ProjectMenu(
            [Contribution(gated)], UiContribution.NodeContext, "Org/Doc", null, Permission.Read, false));

        // And the gate is opt-in — an ungated entry is unaffected by the node's sync state.
        Assert.Single(Project(new UiContribution { Area = "StopSync" }, node: claimed));
    }

    [Fact]
    public void ExcludeViewerHome_SubtractsOnTheViewersOwnHomeOnly()
    {
        // The gate the Edit/Move/Copy/Delete defaults need — the same comparison PinLayoutArea and
        // PresentationLayoutArea already make ("you do not pin yourself to yourself").
        var alice = new MeshNode("alice", "") { NodeType = "User" };
        var gated = new UiContribution
        {
            Area = "Edit",
            Gates = new UiContributionGates { ExcludeViewerHome = true },
        };

        Assert.Empty(Project(gated, node: alice, menuPath: "alice", viewerId: "alice"));
        // Case-insensitive, like every other partition-key comparison on this path.
        Assert.Empty(Project(gated, node: alice, menuPath: "Alice", viewerId: "alice"));
        // Someone ELSE's home is not the viewer's home — this gate alone does not suppress there.
        Assert.Single(Project(gated, node: alice, menuPath: "bob", viewerId: "alice"));
        // No viewer id (anonymous, or a host with no AccessService): there is no home to be on.
        Assert.Single(Project(gated, node: alice, menuPath: "alice", viewerId: null));
    }

    [Fact]
    public void ExcludeViewerHome_IsStrictlyNarrowerThan_ExcludePartitionRoot()
    {
        // The two gate words are NOT interchangeable, and a contribution that means "never on any
        // home" must keep declaring ExcludePartitionRoot: an admin browsing someone else's home
        // still must not get a Delete entry there.
        var alice = new MeshNode("alice", "") { NodeType = "User" };
        var viewerHomeOnly = new UiContribution
            { Area = "Delete", Gates = new UiContributionGates { ExcludeViewerHome = true } };
        var anyRoot = new UiContribution
            { Area = "Delete", Gates = new UiContributionGates { ExcludePartitionRoot = true } };

        Assert.Single(Project(viewerHomeOnly, node: alice, menuPath: "alice", viewerId: "bob"));
        Assert.Empty(Project(anyRoot, node: alice, menuPath: "alice", viewerId: "bob"));
    }

    [Fact]
    public void NodeSettingsTabs_AreASeparateSurface_FromTheGlobalSettingsTabs()
    {
        // 🚨 The context-key decision of #3055, pinned. ONE shared key would list every one of the
        // seven platform tabs already seeded for the GLOBAL settings page (What's New, About,
        // Privacy, Invitations, Inbox, Updates, Published) on EVERY node's settings page — a
        // visible regression shipped in the name of a refactor. Each surface answers its own key.
        var globalTab = new UiContribution
            { Context = UiContribution.SettingsContext, Area = "SettingsAbout", Label = "About" };
        var nodeTab = new UiContribution
            { Context = UiContribution.NodeSettingsContext, Area = "NotificationsTab", Label = "Notifications" };

        Assert.Empty(ProjectNodeSettings(globalTab));
        Assert.Empty(UiContributionProjection.ProjectSettingsTabs([Contribution(nodeTab)], isAdmin: true));

        Assert.Equal("Notifications", Assert.Single(ProjectNodeSettings(nodeTab)).Label);
        Assert.Equal("About", Assert.Single(
            UiContributionProjection.ProjectSettingsTabs([Contribution(globalTab)], isAdmin: false)).Label);

        // A NodeSettings entry is not a node-MENU entry either — the contexts do not bleed.
        Assert.Empty(Project(nodeTab));
    }

    [Fact]
    public void NodeSettingsTabs_CarryKeywords_SoAMigratedTabStaysSearchable()
    {
        // SettingsMenuItemDefinition.Keywords backs the settings SEARCH box. Without a keywords
        // field on the contribution, migrating a tab onto this lane would silently remove it from
        // search — PartitionSyncAdminLayoutArea alone ships fifteen terms.
        var tab = ProjectNodeSettings(new UiContribution
        {
            Context = UiContribution.NodeSettingsContext,
            Area = "PartitionSyncAdmin",
            Label = "Partition Sync",
            LabelKey = "settings.partitionSync",
            Keywords = ["partitions", "sync source", "decouple", "delete space"],
        });

        Assert.Equal(
            new[] { "partitions", "sync source", "decouple", "delete space" },
            Assert.Single(tab).Keywords);
    }

    [Fact]
    public void NodeSettingsTabs_StampRequiredPermission_RatherThanFilteringOnIt()
    {
        // 🚨 The #1962 property. This projection takes NO permission argument on purpose: baking a
        // permission snapshot into a long-lived provider stream is what silently emptied the
        // settings menu. The floor is stamped onto the definition and applied at the render fold.
        var declaresNothing = Assert.Single(ProjectNodeSettings(new UiContribution
            { Context = UiContribution.NodeSettingsContext, Area = "A" }));
        Assert.Equal(Permission.Read, declaresNothing.RequiredPermission);
        // Label falls back to the area when the contribution declares none.
        Assert.Equal("A", declaresNothing.Label);

        var demandsUpdate = Assert.Single(ProjectNodeSettings(new UiContribution
        {
            Context = UiContribution.NodeSettingsContext,
            Area = "B",
            RequiredPermission = Permission.Update,
        }));
        Assert.Equal(Permission.Update, demandsUpdate.RequiredPermission);

        // …and the fold is what subtracts: a Read viewer keeps the first and loses the second, and
        // an anonymous viewer (Permission.None at the fold) loses BOTH — the Read floor is what
        // makes "declares nothing" still mean "not for the logged-out".
        IReadOnlyList<SettingsMenuItemDefinition> both = [declaresNothing, demandsUpdate];
        Assert.Equal(["A"], SettingsMenuItemsExtensions
            .FilterByPermission(both, Permission.Read).Select(i => i.Label));
        Assert.Empty(SettingsMenuItemsExtensions.FilterByPermission(both, Permission.None));
    }

    [Fact]
    public void NodeSettingsTabs_EnforceTheSameClosedGateVocabulary_AsTheNodeMenu()
    {
        var adminTab = new UiContribution
        {
            Context = UiContribution.NodeSettingsContext,
            Area = "AdminTab",
            Gates = new UiContributionGates { AdminOnly = true },
        };
        Assert.Empty(ProjectNodeSettings(adminTab));
        Assert.Single(ProjectNodeSettings(adminTab, isAdmin: true));

        var typeGated = new UiContribution
        {
            Context = UiContribution.NodeSettingsContext,
            Area = "SpaceTab",
            Gates = new UiContributionGates { NodeTypes = ["Space"] },
        };
        Assert.Empty(ProjectNodeSettings(typeGated));
        // Suffix-aware, exactly like the node menu: a plugin-installed "Publish/Space" matches.
        Assert.Single(ProjectNodeSettings(typeGated,
            node: new MeshNode("S", "Org") { NodeType = "Publish/Space" }));

        var homeGated = new UiContribution
        {
            Context = UiContribution.NodeSettingsContext,
            Area = "HomeTab",
            Gates = new UiContributionGates { ExcludeViewerHome = true },
        };
        Assert.Empty(ProjectNodeSettings(homeGated, menuPath: "alice", viewerId: "alice"));
        Assert.Single(ProjectNodeSettings(homeGated, menuPath: "alice", viewerId: "bob"));

        // An empty Area is dropped before any gate runs, on this lane as on every other.
        Assert.Empty(ProjectNodeSettings(new UiContribution
            { Context = UiContribution.NodeSettingsContext, Label = "No target" }));
    }

    [Fact]
    public void NodeSettingsTabs_KeepTheNodeIdAsTheTabId_AndResolveIcons()
    {
        // The node id becomes the /{nodePath}/Settings/{Id} route segment, so a compiled tab that
        // migrates to a same-named seed keeps every bookmarked deep link it had.
        var tab = Assert.Single(UiContributionProjection.ProjectNodeSettingsTabs(
            [(new MeshNode("Notifications", "Admin/UiContribution")
                  { NodeType = UiContributionNodeType.NodeType },
              new UiContribution
              {
                  Context = UiContribution.NodeSettingsContext,
                  Area = "SettingsNotifications",
                  Label = "Notifications",
                  LabelKey = "settings.notifications",
                  Icon = "Alert",
                  Group = "Management",
                  GroupKey = "settings.groupManagement",
                  GroupIcon = "Document",
                  Order = 120,
              })],
            "Org/Doc", SomeNode, isAdmin: false, viewerId: "alice"));

        Assert.Equal("Notifications", tab.Id);
        Assert.Equal(120, tab.Order);
        Assert.Equal("Management", tab.Group);
        Assert.Equal("settings.notifications", tab.LabelKey);
        Assert.Equal("settings.groupManagement", tab.GroupKey);
        var icon = Assert.IsType<Domain.Icon>(tab.Icon);
        Assert.Equal(Domain.Icon.FluentProvider, icon.Provider);
        Assert.Equal("Alert", icon.Id);
        Assert.IsType<Domain.Icon>(tab.GroupIcon);
    }

    /// <summary>
    /// 🚨 The one field the closed vocabulary must NEVER grow: a projected entry always carries a
    /// null <see cref="NodeMenuItemDefinition.Action"/>.
    ///
    /// <para><b>Why this is a security ratchet and not a style rule.</b> <c>Action</c> is a command
    /// id a renderer runs IN PLACE instead of navigating, and its own contract says applicability
    /// stays with the provider that emitted the entry — nothing downstream re-checks
    /// <see cref="NodeMenuItemDefinition.RequiredPermission"/>. So a contribution able to declare
    /// <c>action: "recycle"</c> beside <c>requiredPermission: Read</c> would hand every reader of a
    /// node a button that tears its hub down. That is a WIDENING, and the whole point of the closed
    /// vocabulary is that a contribution can only ever narrow. Behaviour stays compiled — which is
    /// why Recycle is one of the four node-menu defaults that never migrate
    /// (<c>Doc/Architecture/MenuContributionBoundary</c>).</para>
    ///
    /// <para>The control arm is the second assertion: the same projection DOES carry the fields it
    /// is supposed to, so a green here cannot mean "the projection produced nothing".</para>
    /// </summary>
    [Fact]
    public void ProjectedEntry_NeverCarriesAnAction_BehaviourStaysCompiled()
    {
        var item = Assert.Single(Project(new UiContribution
        {
            Area = "MyArea",
            Label = "Mine",
            LabelKey = "menu.mine",
            Icon = "🧩",
            Tooltip = "t",
            Order = 42,
        }));

        Assert.Null(item.Action);
        Assert.False(item.IsAction);

        // Control arm: the projection really did project.
        Assert.Equal("MyArea", item.Area);
        Assert.Equal(42, item.Order);
        Assert.Equal("menu.mine", item.LabelKey);
    }

    /// <summary>
    /// The same ratchet at the DECLARATION: <see cref="UiContribution"/> carries no property that
    /// could name a command. The projection test above pins today's behaviour; this one fails the
    /// moment someone adds the field, which is where the decision actually gets made.
    /// </summary>
    [Fact]
    public void UiContribution_DeclaresNoCommandField()
    {
        var offending = typeof(UiContribution).GetProperties()
            .Select(p => p.Name)
            .Where(n => n is "Action" or "Command" or "ClickAction" or "OnClick")
            .ToArray();

        Assert.True(offending.Length == 0,
            $"UiContribution must not be able to name a command: {string.Join(", ", offending)}. "
            + "Nothing downstream re-checks RequiredPermission for an action entry, so a data-declared "
            + "one widens rather than narrows. See Doc/Architecture/MenuContributionBoundary.");

        // Control arm: the reflection is looking at a type that really has properties.
        Assert.Contains("Area", typeof(UiContribution).GetProperties().Select(p => p.Name));
    }
}
