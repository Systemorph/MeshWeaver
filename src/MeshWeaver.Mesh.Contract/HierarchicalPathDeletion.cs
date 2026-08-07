using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;

namespace MeshWeaver.Mesh;

/// <summary>
/// Pure parent-grouped bottom-up traversal for recursive deletion. Given a
/// root path, a set of paths to delete, and a per-node delete delegate, walks
/// the implicit tree depth-first: for each node, fires off each child's
/// subtree-delete in parallel via <see cref="Observable.Merge{TSource}(System.Collections.Generic.IEnumerable{IObservable{TSource}})"/>,
/// waits for them all to complete, then invokes the delegate for itself.
///
/// <para>The grouping by common parent is implicit in the recursion: siblings
/// share one <c>Observable.Merge</c>, but unrelated branches of the
/// tree progress independently — a leaf at depth 5 doesn't have to wait for
/// an unrelated leaf at the same depth to finish.</para>
///
/// <para><b>Virtual (node-less) levels are traversed, not skipped.</b> A path
/// set routinely contains descendants whose intermediate segments carry no
/// node of their own — satellite dictionaries like <c>{path}/_Thread/{id}</c>,
/// compile-watcher releases at <c>{nodeType}/Release/{version}</c>, source
/// folders at <c>{space}/Source/{file}</c>. The traversal recurses through
/// those virtual levels (grouping descendants by their next path segment) and
/// invokes <c>deleteOne</c> ONLY for paths actually present in the
/// set. The previous shape recursed only into paths present in the set, so an
/// entire branch anchored under a node-less segment was silently never visited
/// — the delete reported success while the branch survived in storage
/// (issue #839).</para>
///
/// <para>Fail-fast semantics: when the per-node delegate fires
/// <c>OnError</c> for some descendant, the per-subtree <c>Observable.Merge</c>
/// propagates the error, sibling subtrees cancel, and the parent is **not**
/// deleted. Partial deletion (some leaves already gone) is the acceptable
/// outcome per the actor model — there is no rollback.</para>
///
/// <para>Pure / testable — no MeshNode loaded, no hub reference, no
/// persistence. Inject a fake <c>deleteOne</c> in tests to verify ordering,
/// parallelism, and error propagation.</para>
/// </summary>
public static class HierarchicalPathDeletion
{
    /// <summary>
    /// Walks the path set bottom-up under <paramref name="rootPath"/> and
    /// invokes <paramref name="deleteOne"/> for each node after its
    /// descendants are deleted.
    /// </summary>
    /// <param name="rootPath">The subtree root. Added to the path set if absent.</param>
    /// <param name="descendantPaths">
    /// Strict descendants of <paramref name="rootPath"/> (i.e., results of an
    /// authoritative storage enumeration). The root itself MUST NOT be included
    /// to avoid an infinite re-entry through the same delete request.
    /// </param>
    /// <param name="deleteOne">
    /// Per-node delete delegate. Returns <c>IObservable&lt;Unit&gt;</c> that
    /// emits once + <c>OnCompleted</c> on success, or <c>OnError</c> on
    /// failure. Called once per path in the set, only after all that path's
    /// descendants have already completed. Never called for virtual
    /// (node-less) intermediate levels.
    /// </param>
    /// <returns>
    /// An observable emitting (once) the ordered list of paths that were
    /// successfully deleted before the operation completed or failed.
    /// On failure, the observable propagates the underlying exception; the
    /// already-recorded successful paths are still emitted via
    /// <c>OnError.Data["DeletedPaths"]</c> for caller bookkeeping.
    /// </returns>
    public static IObservable<IReadOnlyList<string>> DeleteSubtree(
        string rootPath,
        IEnumerable<string> descendantPaths,
        Func<string, IObservable<string>> deleteOne)
    {
        var paths = descendantPaths
            .Where(p => !string.IsNullOrEmpty(p))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)
            .Add(rootPath);

        var deleted = ImmutableList.CreateBuilder<string>();
        return DeleteSubtreeImpl(rootPath, paths, deleteOne, deleted)
            .Select(_ => (IReadOnlyList<string>)deleted.ToImmutable())
            .Catch<IReadOnlyList<string>, Exception>(ex =>
            {
                ex.Data["DeletedPaths"] = (IReadOnlyList<string>)deleted.ToImmutable();
                return Observable.Throw<IReadOnlyList<string>>(ex);
            });
    }

    private static IObservable<string> DeleteSubtreeImpl(
        string nodePath,
        ImmutableHashSet<string> allPaths,
        Func<string, IObservable<string>> deleteOne,
        ImmutableList<string>.Builder deleted)
    {
        var prefix = nodePath + "/";
        // Every direct child LEVEL under nodePath that anchors at least one
        // path in the set — whether or not the level itself is in the set.
        // Grouping by the next path segment (rather than filtering the set for
        // exact depth+1 members) is what carries the traversal across virtual
        // node-less levels (`{path}/_Thread`, `{nodeType}/Release`, …) so the
        // real descendants beneath them are still visited and deleted.
        var childLevels = allPaths
            .Where(p => p.Length > prefix.Length
                && p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                var next = p.IndexOf('/', prefix.Length);
                return next < 0 ? p : p[..next];
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();

        var childOps = childLevels.Count == 0
            ? Observable.Return(string.Empty)
            : Observable
                .Merge(childLevels.Select(c =>
                    DeleteSubtreeImpl(c, allPaths, deleteOne, deleted)))
                .LastOrDefaultAsync();

        return childOps.SelectMany(_ => allPaths.Contains(nodePath)
            ? deleteOne(nodePath)
                .Do(deletedPath =>
                {
                    lock (deleted) deleted.Add(deletedPath);
                })
            // Virtual level: no node lives here — nothing to delete, just
            // propagate completion upward after the descendants are gone.
            : Observable.Return(nodePath));
    }
}
