using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The STATIC integrity check for a set of seeded <see cref="UiContribution"/> nodes — core's
/// equivalent of <c>MeshWeaver.Plugins/scripts/check-menu-contexts.py</c>, expressed as a pure
/// function so a pinning test rather than a script enforces it (#3055).
///
/// <para><b>Why this exists.</b> Every defect it catches is SILENT — the contribution simply never
/// appears, with no error, no warning and not even an area-not-found placeholder to notice:</para>
/// <list type="bullet">
/// <item><description>a <see cref="UiContribution.Context"/> nobody consumes: the entry is
/// projected into a menu that is never rendered. Six shipped entries were dark for nine days that
/// way (Systemorph/MeshWeaver.Plugins#1162) before anyone looked.</description></item>
/// <item><description>an empty <see cref="UiContribution.Area"/>: both projections drop the entry
/// BEFORE any gate runs, so it cannot even be found by turning gates off.</description></item>
/// <item><description>a non-portal-internal <see cref="UiContribution.Href"/>:
/// <c>UiContributionProjection.ResolveHref</c> rejects it and the entry quietly opens the DERIVED
/// area URL instead — it renders, it is clickable, and it goes somewhere else.</description></item>
/// <item><description>a <see cref="MeshNode.NodeType"/> that is not <c>UiContribution</c>: the
/// catalog's <c>nodeType:UiContribution</c> query never returns the node at all.</description></item>
/// <item><description>a missing <see cref="UiContribution.LabelKey"/> beside a
/// <see cref="UiContribution.Label"/>: the entry ships its English label to every German
/// viewer.</description></item>
/// <item><description>two seeds at the SAME path: the catalog is keyed on path, so one silently
/// replaces the other and which one wins depends on query order.</description></item>
/// </list>
///
/// <para>Reusable by any repo that seeds contributions from compiled code — the check belongs with
/// the vocabulary, not with one seed list.</para>
/// </summary>
public static class UiContributionSeedValidation
{
    /// <summary>
    /// The menu contexts the PLATFORM itself consumes. A contribution naming anything else renders
    /// nowhere unless something declares that key — which, for a top-bar menu, is a
    /// <see cref="UiContribution.TopBarContext"/> contribution whose <c>Area</c> IS the new key
    /// (<see cref="Validate(IReadOnlyCollection{MeshNode}, JsonSerializerOptions?, IEnumerable{string}?, IReadOnlyCollection{string}?)"/>
    /// folds those in automatically), and for anything else a caller-supplied extra context.
    ///
    /// <para>Derived from the constants, never re-typed: a renamed context moves this set with it.
    /// The shell's own renderer lives in another repo, so this is deliberately the set core can
    /// PROVE it projects — a satellite passes its own keys as <c>additionalContexts</c> rather
    /// than widening this one.</para>
    /// </summary>
    public static ImmutableHashSet<string> PlatformContexts { get; } =
    [
        UiContribution.NodeContext,
        UiContribution.MeshContext,
        UiContribution.SettingsContext,
        UiContribution.NodeSettingsContext,
        UiContribution.TopBarContext,
        NodeMenuItemsExtensions.AiMenuContext,
        NodeMenuItemsExtensions.GitHubMenuContext,
    ];

    /// <summary>
    /// Validates a seed set, returning one message per problem — empty means clean. Never throws:
    /// the caller (a test, a startup assertion) decides what a problem costs.
    /// </summary>
    /// <param name="seeds">The seeded contribution nodes.</param>
    /// <param name="options">The owning hub's serializer options, when the content may have been
    /// round-tripped through JSON. Null for a COMPILED seed list, whose <c>Content</c> is the CLR
    /// record itself — anything else in that position is reported rather than silently skipped.</param>
    /// <param name="additionalContexts">Context keys this deployment declares beyond
    /// <see cref="PlatformContexts"/>.</param>
    /// <param name="registeredAreas">When supplied, every seed's <c>Area</c> must name one of
    /// these — the "this area is actually registered" half a dangling seed fails.</param>
    public static ImmutableList<string> Validate(
        IReadOnlyCollection<MeshNode> seeds,
        JsonSerializerOptions? options = null,
        IEnumerable<string>? additionalContexts = null,
        IReadOnlyCollection<string>? registeredAreas = null)
    {
        var problems = ImmutableList.CreateBuilder<string>();

        var contents = new List<(MeshNode Node, UiContribution? Content)>(seeds.Count);
        foreach (var seed in seeds)
            contents.Add((seed, ReadContent(seed, options)));

        // A TopBar declaration INTRODUCES a context key (its Area), which entries in the same set
        // may then target. Fold those in before judging any entry's context.
        var known = PlatformContexts;
        if (additionalContexts is not null)
            known = known.Union(additionalContexts.Where(c => !string.IsNullOrEmpty(c)));
        foreach (var (_, content) in contents)
            if (content is { Context: UiContribution.TopBarContext, Area: { Length: > 0 } key })
                known = known.Add(key);

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (node, content) in contents)
        {
            var path = node.Path;
            if (!seenPaths.Add(path))
                problems.Add($"{path}: two seeds share this path — the catalog is keyed on path, so one silently replaces the other");

            if (!UiContributionNodeType.Matches(node.NodeType))
                problems.Add($"{path}: NodeType is '{node.NodeType ?? "(null)"}', not '{UiContributionNodeType.NodeType}' — the catalog query never returns it");

            if (content is null)
            {
                problems.Add($"{path}: Content is not a readable UiContribution (it is {node.Content?.GetType().Name ?? "null"})");
                continue;
            }

            var context = content.Context ?? UiContribution.NodeContext;
            if (!known.Contains(context))
                problems.Add($"{path}: Context '{context}' is declared by nobody — the entry renders NOWHERE, silently. Known: {string.Join(", ", known.OrderBy(c => c, StringComparer.Ordinal))}");

            if (content.Area is not { Length: > 0 } area)
            {
                problems.Add($"{path}: Area is empty — both projections drop the entry before any gate runs");
            }
            else if (registeredAreas is not null && !registeredAreas.Contains(area))
            {
                problems.Add($"{path}: Area '{area}' is not a registered layout area — the tab renders the not-found placeholder");
            }

            // The gate applies to the RESOLVED href, so judge it the way the projection will: with
            // the {node} token substituted for a representative path.
            if (content.Href is { Length: > 0 } href
                && UiContributionProjection.ResolveHref(href, path) is null)
            {
                problems.Add($"{path}: Href '{href}' is not portal-internal — the projection discards it and the entry quietly opens the derived area URL instead");
            }

            if (content.Label is { Length: > 0 } && content.LabelKey is not { Length: > 0 })
                problems.Add($"{path}: Label '{content.Label}' has no LabelKey — it ships English to every non-English viewer");

            if (content.Group is { Length: > 0 } && content.GroupKey is not { Length: > 0 })
                problems.Add($"{path}: Group '{content.Group}' has no GroupKey — the group header ships English to every non-English viewer");
        }

        return problems.ToImmutable();
    }

    /// <summary>
    /// Reads a seed's content. With serializer options this is the ordinary bad-data-tolerant
    /// <c>As&lt;T&gt;</c> read; without them the content must ALREADY be the CLR record, which is
    /// the only shape a compiled seed list can hold — and when it is not, the caller gets a
    /// PROBLEM naming the runtime type rather than a silent null.
    /// </summary>
    private static UiContribution? ReadContent(MeshNode node, JsonSerializerOptions? options)
        => options is not null
            ? node.Content.As<UiContribution>(options, what: node.Path)
            : node.Content as UiContribution;
}
