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
            return workspace.GetMeshNodeStream(path)
                // ContentAs is the bad-data-tolerant read: a stale/unknown $type degrades to a raw
                // JsonElement and is recovered here, and an unrecoverable one logs LOUD and returns
                // null — so a corrupt catalog names itself in the log and the menu keeps rendering.
                .Select(node => node.ContentAs<MenuPresentation>(options, logger))
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
            // GetMeshNodeStream throws synchronously on hubs with no MeshDataSource (scratch hubs,
            // startup, mid-dispose) — same guard the default provider uses.
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

        // 1. Patch + drop hidden.
        var patched = new List<(NodeMenuItemDefinition Item, string? Parent)>(items.Count);
        foreach (var item in items)
        {
            if (item.Area is not { Length: > 0 } area || !index.TryGetValue(area, out var entry))
            {
                patched.Add((item, null));
                continue;
            }

            if (entry.Hidden)
                continue;

            patched.Add((entry.Apply(item, locale), entry.Parent));
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
    private static IEnumerable<NodeMenuItemDefinition> Sort(IEnumerable<NodeMenuItemDefinition> items)
        => items
            .OrderBy(i => i.Order)
            .ThenBy(i => i.Label, StringComparer.Ordinal)
            .ThenBy(i => i.Area, StringComparer.Ordinal);
}
