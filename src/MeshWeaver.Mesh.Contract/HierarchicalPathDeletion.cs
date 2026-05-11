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
/// share one <see cref="Observable.Merge"/>, but unrelated branches of the
/// tree progress independently — a leaf at depth 5 doesn't have to wait for
/// an unrelated leaf at the same depth to finish.</para>
///
/// <para>Fail-fast semantics: when the per-node delegate fires
/// <c>OnError</c> for some descendant, the per-subtree <see cref="Observable.Merge"/>
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
    /// Strict descendants of <paramref name="rootPath"/> (i.e., results of a
    /// <c>scope:descendants</c> query). The root itself MUST NOT be included
    /// to avoid an infinite re-entry through the same delete request.
    /// </param>
    /// <param name="deleteOne">
    /// Per-node delete delegate. Returns <c>IObservable&lt;Unit&gt;</c> that
    /// emits once + <c>OnCompleted</c> on success, or <c>OnError</c> on
    /// failure. Called once per path in the set, only after all that path's
    /// descendants have already completed.
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
        var children = allPaths
            .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && p.IndexOf('/', prefix.Length) < 0)
            .ToImmutableList();

        var childOps = children.Count == 0
            ? Observable.Return(string.Empty)
            : Observable
                .Merge(children.Select(c =>
                    DeleteSubtreeImpl(c, allPaths, deleteOne, deleted)))
                .LastOrDefaultAsync();

        return childOps.SelectMany(_ => deleteOne(nodePath)
            .Do(deletedPath =>
            {
                lock (deleted) deleted.Add(deletedPath);
            }));
    }
}
