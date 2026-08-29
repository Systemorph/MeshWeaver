using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Applies the data-defined <see cref="MenuPresentation"/> catalog over the items the compiled
/// providers emit — the seam that turns "change a menu label" from a CI + image + rollout cycle
/// into a node edit.
///
/// <para>The overlay is deliberately <b>subtractive and cosmetic only</b>: it can re-word, re-icon,
/// re-order, group and hide an entry, but it can never introduce one. That is not a simplification
/// — it is the security boundary. Node-menu permission filtering is enforced by providers
/// <i>not emitting</i> an item (<see cref="NodeMenuItemDefinition.RequiredPermission"/> is carried
/// for display and equality, but nothing filters on it), so an entry conjured from data would have
/// no permission gate at all. Overriding only what a provider already decided to show keeps every
/// visibility decision in compiled, CI-tested code while making the wording and shape editable at
/// runtime.</para>
/// </summary>
internal static class MenuPresentationOverlay
{
    /// <summary>
    /// The projection a catalog read needs. <c>content</c> is named deliberately: a <c>select:</c>
    /// that omits it yields a node whose <c>Content</c> is silently null, which would read as
    /// "no catalog" for a catalog that exists.
    /// </summary>
    internal const string CatalogProjection = "select:path,id,namespace,name,nodeType,content";

    /// <summary>
    /// The synced-query id for <paramref name="context"/>'s catalog — <c>{type}|{path}</c>, the same
    /// convention <c>UpdatePolicyNodeType</c> and <c>NotificationSettingsNodeType</c> register under,
    /// so a future seeding path can share this registration instead of opening a second synced query
    /// for the same node. Stable per context and never composed per call, so the process-wide query
    /// cache holds ONE upstream per menu context for the life of the process — see
    /// <see cref="CatalogQuery"/> for why that matters here.
    /// </summary>
    internal static string CatalogQueryId(string context)
        => $"{nameof(MenuPresentation)}|{MenuPresentation.PathFor(context)}";

    /// <summary>
    /// The catalog read for <paramref name="context"/>: an EXACT, partition-anchored read of the one
    /// node at <see cref="MenuPresentation.PathFor"/>.
    ///
    /// <para>Anchored by construction — <c>path:Admin/Menu/{context}</c> carries a concrete first
    /// segment, so every backend routes it to the <c>admin</c> partition alone rather than fanning
    /// out. (A <c>namespace:Admin</c>-shaped read could not: <c>admin</c> is deliberately excluded
    /// from cross-schema search, so a path is the only way in.) It is deliberately NOT filtered by
    /// <c>nodeType:</c> — <c>MenuPresentation</c> is a registered CONTENT type, not a NodeType, so
    /// the node's own <c>nodeType</c> is whatever its author chose; the path is already unique and
    /// <c>ContentAs&lt;MenuPresentation&gt;</c> is what decides whether it is a catalog.</para>
    /// </summary>
    internal static string CatalogQuery(string context)
        => $"path:{MenuPresentation.PathFor(context)} {CatalogProjection}";

    /// <summary>
    /// The catalog for <paramref name="context"/> as a live stream, or a stream of <c>null</c> when
    /// there is none. Fail-safe in every direction: a missing node, an unreadable partition, a
    /// deserialization fault or a hub without a mesh data source all degrade to "no catalog", which
    /// means the compiled menu renders unchanged.
    ///
    /// <para>There is deliberately NO seeded catalog. "No entry for this area" is the normal
    /// resting state, not drift to be repaired — which is what keeps this out of the failure mode
    /// that drove a reconciled per-instance config node to version 254,760. It also means a
    /// framework release that adds a menu entry needs no catalog migration: the new entry simply
    /// renders its compiled presentation until someone chooses to override it. Seeding a copy of the
    /// compiled presentation would instead freeze it, and silently outrank every later change to it.
    /// </para>
    /// </summary>
    public static IObservable<MenuPresentation?> CatalogStream(
        IWorkspace workspace, JsonSerializerOptions options, string context, ILogger? logger)
    {
        var path = MenuPresentation.PathFor(context);
        try
        {
            // 🚨 A synced QUERY (empty-on-absent), never a GetMeshNodeStream point-read — and the
            // reason is the sentence three paragraphs up: there is deliberately no seeded catalog,
            // so ABSENT IS THE NORMAL STATE for every context on almost every mesh. A point-read of
            // a node that does not exist is not free and not quiet: it resolves through the router,
            // finds nothing, and answers NotFound — one `[ROUTE] NotFound: No node found at
            // 'Admin/Menu/{context}'` warning plus a DeliveryFailure NACK per probe, damped only by
            // the stream cache's negative-cache window, which expires and re-probes forever.
            //
            // And the probe rate is not once per circuit. RenderMenus is a GLOBAL predicate
            // renderer (`WithRenderer(_ => true, …)`), so it runs on EVERY area render, and it
            // installs this stream with ReplaceDisposable — dispose the old subscription, open a
            // new one — once per menu CONTEXT per render. Contexts are data-driven
            // (UiContributionCatalog), so installing a plugin that declares a TopBar menu adds one
            // more permanently-missing routed lookup to every area render on the mesh. Issue #2640
            // names these misses directly.
            //
            // This is not a new rule, it is an existing one applied where it was missed.
            // PlatformUpdateStatus.Observe states it outright: "Absence is detected with the
            // storm-safe existence GetQuery (empty-on-absent) — NEVER a point GetMeshNodeStream
            // probe of a maybe-absent path, which NotFound-resubscribe-storms". That surface reads
            // its maybe-absent node at most a few times a day; this one read its maybe-absent node
            // once per context per area render.
            //
            // A query answers "no rows" for an absent node — no NotFound, no NACK, no negative
            // cache — and `GetQuery` caches its upstream process-wide under a stable id with
            // Replay(1)+AutoConnect(1), so the per-render re-subscribe lands on a warm replay
            // instead of re-probing. Liveness is unchanged, which is the point: it is still a
            // synced query fed by the change feed, so an admin creating Admin/Menu/Node updates
            // every open menu with no recycle — the "seconds, no build, no rollout" contract in
            // Doc/Architecture/MenuAsData. Same substitution, and the same rationale, as
            // NotificationService.ReadSettings and NotificationSettingsNodeType.EnsureExists.
            return workspace.GetQuery(CatalogQueryId(context), CatalogQuery(context))
                // ContentAs is the bad-data-tolerant read: a stale/unknown $type degrades to a raw
                // JsonElement and is recovered here, and an unrecoverable one logs LOUD and returns
                // null — so a corrupt catalog names itself in the log and the menu keeps rendering.
                .Select(nodes => nodes
                    .Select(node => node.ContentAs<MenuPresentation>(options, logger))
                    .FirstOrDefault(catalog => catalog is not null))
                .Catch<MenuPresentation?, Exception>(ex =>
                {
                    logger?.LogDebug(ex,
                        "No menu catalog stream for '{Path}'; using compiled menu defaults for context '{Context}'",
                        path, context);
                    return Observable.Return<MenuPresentation?>(null);
                })
                .StartWith((MenuPresentation?)null);
        }
        catch (Exception ex)
        {
            // GetQuery resolves IMeshNodeStreamCache eagerly and throws synchronously on hubs that
            // have none (scratch hubs, startup, mid-dispose) — the same guard the point-read needed
            // for the same class of caller, and the same one the default provider uses.
            logger?.LogDebug(ex, "Menu catalog unavailable for context '{Context}'", context);
            return Observable.Return<MenuPresentation?>(null);
        }
    }

    /// <summary>
    /// Overlays <paramref name="catalog"/> onto <paramref name="items"/> for a viewer in
    /// <paramref name="locale"/>: per-entry text/icon/order patch, <c>Hidden</c> removal, one level
    /// of <c>Parent</c> nesting, then a re-sort (the providers' sort ran before any override, so a
    /// re-ordered entry would otherwise keep its compiled position).
    /// </summary>
    public static IReadOnlyCollection<NodeMenuItemDefinition> Apply(
        IReadOnlyCollection<NodeMenuItemDefinition> items,
        MenuPresentation? catalog,
        string locale,
        Action<string>? onSkipped = null)
    {
        if (items.Count == 0)
            return items;

        var index = catalog?.Index(onSkipped);
        if (index is null || index.Count == 0)
            return items;

        // 1. Patch + drop hidden — at EVERY depth, not just the top level.
        var patched = new List<(NodeMenuItemDefinition Item, string? Parent)>(items.Count);
        foreach (var item in items)
        {
            if (item.Area is not { Length: > 0 } area || !index.TryGetValue(area, out var entry))
            {
                patched.Add((PatchDescendants(item, index, locale), null));
                continue;
            }

            if (entry.Hidden)
                continue;

            patched.Add((PatchDescendants(entry.Apply(item, locale), index, locale), entry.Parent));
        }

        // 2. Nest. A parent is matched by Area among the items that survived; an unresolvable or
        //    nested-parent reference leaves the child top-level and is reported — never dropped.
        var byArea = new Dictionary<string, NodeMenuItemDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, parent) in patched)
            if (parent is null && item.Area is { Length: > 0 } a)
                byArea.TryAdd(a, item);

        var childrenByParent = new Dictionary<string, List<NodeMenuItemDefinition>>(StringComparer.OrdinalIgnoreCase);
        var top = new List<NodeMenuItemDefinition>(patched.Count);

        foreach (var (item, parent) in patched)
        {
            if (parent is not { Length: > 0 })
            {
                top.Add(item);
                continue;
            }

            if (string.Equals(parent, item.Area, StringComparison.OrdinalIgnoreCase))
            {
                onSkipped?.Invoke($"menu entry '{item.Area}' names itself as Parent; kept at top level");
                top.Add(item);
                continue;
            }

            if (!byArea.ContainsKey(parent))
            {
                onSkipped?.Invoke(
                    $"menu entry '{item.Area}' names Parent '{parent}', which no visible menu item provides; kept at top level");
                top.Add(item);
                continue;
            }

            if (!childrenByParent.TryGetValue(parent, out var bucket))
                childrenByParent[parent] = bucket = [];
            bucket.Add(item);
        }

        if (childrenByParent.Count > 0)
            for (var i = 0; i < top.Count; i++)
                if (top[i].Area is { Length: > 0 } a && childrenByParent.TryGetValue(a, out var kids))
                    top[i] = top[i] with
                    {
                        Children = [.. Sort(kids.Concat(top[i].Children ?? []))],
                    };

        // Exactly ONE level of nesting, by construction: children are collected from the flat
        // patched set and never re-parented, so a parent cycle is structurally impossible rather
        // than merely guarded against.

        // 3. Re-sort: the provider aggregation sorted the COMPILED order, before any override.
        return [.. Sort(top)];
    }

    /// <summary>
    /// Stable order matching the provider aggregation's comparer — Order, then Label, then Area —
    /// but as a stable <c>OrderBy</c> rather than a sorted SET: two entries can be patched into the
    /// same Order, and a set would silently drop one of them.
    /// </summary>
    /// <summary>
    /// Applies the catalog to children a PROVIDER already nested, recursively.
    ///
    /// <para>The catalog's contract is "every entry is addressable by its stable <c>Area</c>", and that
    /// has to hold at every depth — otherwise moving entries into a compiled group would quietly make
    /// them un-editable. That is exactly what grouping the export block under 📦 Export would have done:
    /// PDF / Email / DOCX are no longer top-level, so a top-level-only overlay could no longer re-word,
    /// re-icon, re-order or hide any of them.</para>
    ///
    /// <para><b>Re-parenting is deliberately NOT applied here.</b> <c>Parent</c> stays a top-level-only
    /// operation, so nesting remains exactly one level deep by construction and a parent cycle stays
    /// structurally impossible. A nested entry may be re-dressed or hidden; it cannot be moved.</para>
    /// </summary>
    private static NodeMenuItemDefinition PatchDescendants(
        NodeMenuItemDefinition item,
        IReadOnlyDictionary<string, MenuEntryPresentation> index,
        string locale)
    {
        if (item.Children is not { Count: > 0 } children)
            return item;

        var patched = new List<NodeMenuItemDefinition>(children.Count);
        foreach (var child in children)
        {
            if (child.Area is { Length: > 0 } area && index.TryGetValue(area, out var entry))
            {
                if (entry.Hidden)
                    continue;
                patched.Add(PatchDescendants(entry.Apply(child, locale), index, locale));
                continue;
            }

            patched.Add(PatchDescendants(child, index, locale));
        }

        return item with { Children = [.. Sort(patched)] };
    }

    private static IEnumerable<NodeMenuItemDefinition> Sort(IEnumerable<NodeMenuItemDefinition> items)
        => items
            .OrderBy(i => i.Order)
            .ThenBy(i => i.Label, StringComparer.Ordinal)
            .ThenBy(i => i.Area, StringComparer.Ordinal);
}
