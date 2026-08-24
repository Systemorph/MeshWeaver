using MeshWeaver.Mesh;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Storm-resistant fold of a live <c>IMeshService.Query&lt;MeshNode&gt;</c> result stream for
/// <see cref="MeshSearchView"/>. Tracks the current result SET keyed by path and reports — per
/// <see cref="QueryResultChange{T}"/> — whether the visible set changed (a <i>structural</i>
/// change), so the view rebuilds groups + re-renders ONLY when the set actually changes.
/// <para>
/// A content-only <see cref="QueryChangeType.Updated"/> is intentionally a no-op. The top-bar AI
/// menu opens <c>/search?q=nodeType:Thread&amp;groupBy=Namespace</c> (and Models / Agents / Skills)
/// — an UNSCOPED query with no <c>namespace:</c> filter, so it subscribes to EVERY partition's
/// change feed. Every content write to any matching node anywhere then fires an <c>Updated</c>;
/// re-grouping + re-rendering the whole result grid on each of those (the old <c>LoadResults</c>
/// behaviour) turned the catalog page into a re-render firehose that slowed the whole app. Result
/// cards databind their own content via <c>LayoutAreaView</c>, so the list never needs a
/// content-update re-render — only add / remove / reset of the set does.
/// </para>
/// Single-subscription consumer: every emission from one query subscription is folded here in
/// order (the provider serialises its change feed via <c>Concat</c>), so no internal locking is
/// required. A new query creates a fresh accumulator.
/// </summary>
internal sealed class SearchResultAccumulator
{
    private readonly List<MeshNode> _nodes = new();
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Current result set, in insertion order (Initial order, then Added order).</summary>
    public IReadOnlyList<MeshNode> Nodes => _nodes;

    /// <summary>
    /// Folds one change into the set. Returns <see langword="true"/> when the visible SET of nodes
    /// changed and the caller should rebuild groups + re-render; <see langword="false"/> for a
    /// content-only update that must NOT trigger a re-render.
    /// </summary>
    public bool Apply(QueryResultChange<MeshNode> change)
    {
        switch (change.ChangeType)
        {
            case QueryChangeType.Initial:
            case QueryChangeType.Reset:
                // Reset the set. Dedupe by path in case an Initial payload carries the same path
                // twice (UNION ALL across partitions can surface a path from two providers).
                _paths.Clear();
                _nodes.Clear();
                foreach (var n in change.Items)
                    if (!string.IsNullOrEmpty(n.Path) && _paths.Add(n.Path))
                        _nodes.Add(n);
                // Always (re)render the first frame and any reconnection reset.
                return true;

            case QueryChangeType.Added:
            {
                var changed = false;
                foreach (var n in change.Items)
                    if (!string.IsNullOrEmpty(n.Path) && _paths.Add(n.Path))
                    {
                        _nodes.Add(n);
                        changed = true;
                    }
                return changed;
            }

            case QueryChangeType.Removed:
            {
                var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in change.Items)
                    if (!string.IsNullOrEmpty(n.Path) && _paths.Remove(n.Path))
                        removed.Add(n.Path);
                if (removed.Count == 0)
                    return false;
                _nodes.RemoveAll(n => n.Path is not null && removed.Contains(n.Path));
                return true;
            }

            // Content-only — cards databind their own content; never a list re-render.
            case QueryChangeType.Updated:
            default:
                return false;
        }
    }
}
