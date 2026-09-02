using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The <c>UiContribution</c> node type — a MENU ENTRY contributed as mesh DATA (design #1645, the
/// WS7 composition-first lane). A contribution adds an entry to one of the shell's menu contexts
/// (<c>Node</c>, <c>Mesh</c>, <c>Settings</c> — the GLOBAL settings page, <c>NodeSettings</c> —
/// the PER-NODE settings page, <c>TopBar</c>, <c>AI</c>) pointing at a
/// layout AREA the contributing plugin already ships — rendering stays the existing layout
/// pipeline; contributions never introduce a render surface.
///
/// <para><b>The security boundary</b> (the reason <c>MenuPresentationOverlay</c> is cosmetic-only
/// does NOT apply here): visibility is enforced by the COMPILED aggregator against a CLOSED gate
/// vocabulary — <see cref="UiContribution.RequiredPermission"/> is checked against the viewer's
/// LIVE effective permissions, and <see cref="UiContributionGates"/> against the node's shape. A
/// contribution can only ever NARROW its own visibility; it cannot widen anything, and any
/// visibility rule beyond the vocabulary stays in code (a per-NodeType in-mesh delegate or a
/// compiled provider).</para>
/// </summary>
public static class UiContributionNodeType
{
    /// <summary>The NodeType value identifying UI-contribution nodes.</summary>
    public const string NodeType = "UiContribution";

    /// <summary>
    /// True for the built-in type OR a plugin-installed variant whose dynamic identity ends in
    /// <c>/UiContribution</c> — the platform's suffix-aware convention (see
    /// <c>SlideNodeType.Matches</c>).
    /// </summary>
    public static bool Matches(string? nodeType) =>
        nodeType == NodeType
        || nodeType?.EndsWith("/" + NodeType, StringComparison.Ordinal) == true;

    /// <summary>Registers the built-in <c>UiContribution</c> node type on the mesh builder.</summary>
    public static TBuilder AddUiContributionType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(new MeshNode(NodeType)
        {
            Name = "UI Contribution",
            HubConfiguration = config => config
                .AddMeshDataSource(s => s.WithContentType<UiContribution>()),
        });
        // Platform plumbing, not pickable content — never offered in UCR autocomplete.
        builder.AddAutocompleteExcludedTypes(NodeType);
        builder.WithMeshType(typeof(UiContribution), nameof(UiContribution));
        return builder;
    }
}

/// <summary>
/// The content of a <c>UiContribution</c> node — one contributed menu entry. Immutable; every
/// mutation goes through <c>workspace.GetMeshNodeStream(path).Update(...)</c>.
/// </summary>
public record UiContribution
{
    /// <summary>The <c>Node</c> menu context — per-node operations (the ✏️/🗑️ dropdown).</summary>
    public const string NodeContext = "Node";
    /// <summary>The <c>Mesh</c> menu context — mesh-level operations (Create/Import/Export).</summary>
    public const string MeshContext = "Mesh";
    /// <summary>
    /// The GLOBAL settings tabs context (<c>/_Setting/GlobalSettings/{Id}</c>) — node-independent,
    /// platform-wide tabs. Projects into <c>GlobalSettingsMenuItemDefinition</c>.
    /// </summary>
    public const string SettingsContext = "Settings";

    /// <summary>
    /// The PER-NODE settings tabs context (<c>/{nodePath}/Settings/{Id}</c>) — tabs anchored on
    /// the node whose settings page is open. Projects into <c>SettingsMenuItemDefinition</c>.
    ///
    /// <para><b>Why a SECOND key rather than reusing <see cref="SettingsContext"/>.</b> The two
    /// surfaces are different pages with different definition types, different routes and
    /// different content-builder signatures (the per-node builder receives the anchoring
    /// <see cref="MeshNode"/>; the global one has no node at all). One shared key would make every
    /// contribution appear on BOTH — the seven platform tabs already seeded for the global surface
    /// (What's New, About, Privacy, Invitations, Inbox, Updates, Published) would suddenly list on
    /// every node's settings page, which is a visible regression rather than a migration. Two keys
    /// also make the surfaces independently gateable, which is the property the whole
    /// contribution lane exists for: a plugin decides which page its tab belongs on. A tab that
    /// genuinely belongs on both is two contributions, and says so.</para>
    ///
    /// <para>The per-node lane is additionally the only one that carries
    /// <see cref="UiContribution.Keywords"/> and <see cref="UiContribution.RequiredPermission"/>
    /// through to the rendered tab — the per-node settings page has a search box and a node to
    /// bind permissions to; the global page has neither.</para>
    /// </summary>
    public const string NodeSettingsContext = "NodeSettings";

    /// <summary>
    /// The top-bar MENU-DECLARATION context: a contribution here declares a whole NEW top-bar
    /// dropdown. <see cref="Area"/> names the new menu's context KEY — entries target that key as
    /// their own <see cref="Context"/> — and Label/LabelKey, Icon, Order and Tooltip style the
    /// menu button. The closed gate vocabulary applies to the declaration like any entry (an
    /// <c>AdminOnly</c> menu disappears wholesale for non-admins), and a menu with no visible
    /// entries renders nothing.
    /// </summary>
    public const string TopBarContext = "TopBar";

    /// <summary>
    /// Which menu the entry contributes to: <c>Node</c>, <c>Mesh</c>, <c>Settings</c> (the GLOBAL
    /// settings page), <c>NodeSettings</c> (the PER-NODE settings page), <c>TopBar</c>, <c>AI</c>
    /// or any key a <c>TopBar</c> declaration introduces. Unset ⇒ <c>Node</c>.
    ///
    /// <para>🚨 A context nobody consumes renders NOWHERE — no error, no warning, not even an
    /// area-not-found placeholder. <see cref="UiContributionSeedValidation"/> is the static check
    /// that catches a mistyped or retired context before it ships dark.</para>
    /// </summary>
    public string? Context { get; init; }

    /// <summary>Display text. Prefer <see cref="LabelKey"/> for entries needing translation.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Localization key for <see cref="Label"/> (resolved against the shared catalog at
    /// aggregation, like every compiled item's <c>LabelKey</c>). One localization story — never a
    /// second mechanism.
    /// </summary>
    public string? LabelKey { get; init; }

    /// <summary>Optional icon — emoji string or SVG URL.</summary>
    public string? Icon { get; init; }

    /// <summary>
    /// The layout AREA the entry opens — an area the contributing plugin ships. The standard
    /// area-not-found placeholder renders if it is missing, exactly like any dangling area link.
    /// </summary>
    public string? Area { get; init; }

    /// <summary>
    /// Optional explicit navigation URL, overriding the URL derived from <see cref="Area"/> on the
    /// anchoring node — the shape catalog-style entries use (absolute portal paths, search URLs).
    /// Purely navigational: what the URL opens still renders through the ordinary layout pipeline
    /// and its own access gates; the closed gate vocabulary here only NARROWS who sees the link.
    /// </summary>
    public string? Href { get; init; }

    /// <summary>Optional hover tooltip. Prefer <see cref="TooltipKey"/> for translated text.</summary>
    public string? Tooltip { get; init; }

    /// <summary>Localization key for <see cref="Tooltip"/>.</summary>
    public string? TooltipKey { get; init; }

    /// <summary>Sort order within the menu (lower = earlier). Default 100 — after the built-ins.</summary>
    public int Order { get; init; } = 100;

    /// <summary>Optional group header (settings tabs) — entries sharing a group nest under it.</summary>
    public string? Group { get; init; }

    /// <summary>Localization key for <see cref="Group"/>.</summary>
    public string? GroupKey { get; init; }

    /// <summary>Optional icon for the group header; the first non-null in a group wins.</summary>
    public string? GroupIcon { get; init; }

    /// <summary>
    /// Extra SEARCH terms describing the fields/content INSIDE the contributed tab — the data
    /// equivalent of <c>SettingsMenuItemDefinition.Keywords</c>, and the reason a migrating tab
    /// does not silently vanish from settings search. The per-node settings page matches a query
    /// against Label, Group AND these terms, so a viewer finds a setting by what is in it, not
    /// only by the section's name (<c>PartitionSyncAdminLayoutArea</c> ships fifteen: "partitions",
    /// "sync source", "decouple", "delete space", …).
    ///
    /// <para>Consumed by <see cref="NodeSettingsContext"/> only — the global settings page has no
    /// search box and <c>GlobalSettingsMenuItemDefinition</c> has no keyword slot; declaring them
    /// on a <see cref="SettingsContext"/> contribution is harmless but inert.</para>
    ///
    /// <para>These are user-VISIBLE search terms: a contribution serving a localized portal
    /// should list the terms of the languages it serves, since there is no per-key translation
    /// lane for a free-text search vocabulary (the same shape the compiled tabs use).</para>
    /// </summary>
    public ImmutableList<string>? Keywords { get; init; }

    /// <summary>
    /// The permission the VIEWER must hold on the node for the entry to appear — enforced by the
    /// compiled aggregator against the live effective-permission stream (never trusted from
    /// data alone). Unset ⇒ <see cref="Permission.Read"/>; contributions can never demand less
    /// than Read.
    /// </summary>
    public Permission RequiredPermission { get; init; } = Permission.Read;

    /// <summary>Node-shape gates further narrowing where the entry appears.</summary>
    public UiContributionGates? Gates { get; init; }
}

/// <summary>
/// The CLOSED node-shape gate vocabulary for <see cref="UiContribution"/> — every gate can only
/// NARROW visibility. Rules beyond this vocabulary belong in code.
/// </summary>
public record UiContributionGates
{
    /// <summary>
    /// Restrict to nodes whose NodeType matches one of these — suffix-aware (<c>"Slide"</c>
    /// matches <c>Publish/Slide</c>), the platform's <c>Matches</c> semantics. Empty/null = every
    /// node type.
    /// </summary>
    public ImmutableList<string>? NodeTypes { get; init; }

    /// <summary>Never on a protected partition root (a user's home) — the same predicate the
    /// built-in Edit/Move/Copy/Delete suppression uses.</summary>
    public bool ExcludePartitionRoot { get; init; }

    /// <summary>Only for platform admins (<c>hub.IsGlobalAdmin()</c> — the ONE admin predicate).</summary>
    public bool AdminOnly { get; init; }

    /// <summary>
    /// Only on nodes still PARTICIPATING in static-repo synchronization — <see
    /// cref="MeshNode.SyncBehavior"/> is <see cref="SyncBehavior.Include"/>. The gate the
    /// "Stop synchronization" entry needs (design #1645): once a viewer has claimed a node
    /// (<see cref="SyncBehavior.ExcludeThisOnly"/> / <see cref="SyncBehavior.ExcludeThisAndChildren"/>)
    /// there is nothing left to stop.
    ///
    /// <para>Narrowing only, like every gate here. The INVERSE ("only on claimed nodes", which the
    /// compiled "Resume synchronization" branch renders) is deliberately NOT in the vocabulary —
    /// a second gate word is a separate decision, and the vocabulary stays closed.</para>
    /// </summary>
    public bool SyncedOnly { get; init; }

    /// <summary>
    /// Never on the VIEWER'S OWN home — the node whose path is the viewer's own partition key.
    /// The same predicate <c>PinLayoutArea</c> and <c>PresentationLayoutArea</c> already apply
    /// ("you do not pin yourself to yourself"; "hiding your own home empties the page you are
    /// reading"), and the gate the Edit/Move/Copy/Delete defaults need when they migrate.
    ///
    /// <para>Strictly narrower than <see cref="ExcludePartitionRoot"/>, and NOT a replacement for
    /// it: <c>ExcludePartitionRoot</c> suppresses on ANY user's home (so an admin browsing someone
    /// else's home still cannot Delete it from the menu), while this one suppresses only on the
    /// viewer's own. Declare both when both apply.</para>
    /// </summary>
    public bool ExcludeViewerHome { get; init; }
}
