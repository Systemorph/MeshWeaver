using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The <c>UiContribution</c> node type — a MENU ENTRY contributed as mesh DATA (design #1645, the
/// WS7 composition-first lane). A contribution adds an entry to one of the shell's menu contexts
/// (<c>Node</c>, <c>Mesh</c>, <c>Settings</c>, <c>AiMenu</c>, <c>SidePanel</c>) pointing at a
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
    /// <summary>The global settings tabs context.</summary>
    public const string SettingsContext = "Settings";

    /// <summary>
    /// Which menu the entry contributes to: <c>Node</c>, <c>Mesh</c>, <c>Settings</c>,
    /// <c>AiMenu</c> or <c>SidePanel</c>. Unset ⇒ <c>Node</c>.
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

    /// <summary>Sort order within the menu (lower = earlier). Default 100 — after the built-ins.</summary>
    public int Order { get; init; } = 100;

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
}
