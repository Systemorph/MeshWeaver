using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Messaging;

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

    /// <summary>
    /// <see cref="DeleteSubtree"/> with at most <paramref name="maxConcurrentDeletes"/> <paramref name="deleteOne"/> legs in
    /// flight across the WHOLE tree at once.
    ///
    /// <para><b>Why the bound is global, not per level</b> (issue #6351). The traversal fans every
    /// sibling set out with an unbounded <c>Merge</c>, and the levels nest, so a wide subtree
    /// subscribes one leg per leaf simultaneously: the 1,383-path <c>Marketing</c> delete on the
    /// control portal (2026-10-09 13:38:58Z) put every one of its leaf commits on the process-wide
    /// cap-1 <c>pg:Postgres</c> write pool at the same instant — 219 waiting there at the timeout,
    /// 1,325 admissions waiting at least a second inside the one stage, and 764 waiting for the
    /// 1,060-path <c>SocialMedia</c> delete 45 minutes later. Each leaf's OWN no-progress watchdog
    /// started when its hub took the request, so a leaf queued behind hundreds of its siblings'
    /// writes made "no progress" for its whole budget while the cascade it belonged to was
    /// removing rows steadily, and the leaf's timeout failed the whole delete. The backlog was the
    /// operation's own. A per-level cap would not stop that, because nested levels multiply; one
    /// lane shared by every leg does.</para>
    ///
    /// <para><b>Ordering and failure are unchanged.</b> A node is still admitted only once all its
    /// descendants have completed, unrelated branches still progress independently, and the first
    /// failing leg still fails the traversal and cancels every running and queued leg — a queued leg
    /// whose subscriber has gone never starts. The lane cannot deadlock: a leg waits for nothing but
    /// its own delete, and a parent is admitted only after its children have released their
    /// slots.</para>
    /// </summary>
    /// <param name="rootPath">The subtree root. Added to the path set if absent.</param>
    /// <param name="descendantPaths">Strict descendants of <paramref name="rootPath"/>.</param>
    /// <param name="deleteOne">Per-node delete delegate, as for the unbounded overload.</param>
    /// <param name="maxConcurrentDeletes">Most <paramref name="deleteOne"/> legs subscribed at once,
    /// across the whole tree; at least 1.</param>
    /// <returns>As for the unbounded overload.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxConcurrentDeletes"/> is
    /// less than 1.</exception>
    public static IObservable<IReadOnlyList<string>> DeleteSubtreeBounded(
        string rootPath,
        IEnumerable<string> descendantPaths,
        Func<string, IObservable<string>> deleteOne,
        int maxConcurrentDeletes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentDeletes, 1);
        return Observable.Create<IReadOnlyList<string>>(observer =>
        {
            // ONE admission lane for every leg of this traversal. Its inners never fault — each
            // leg's outcome is MATERIALIZED and handed to that leg's own subscriber — so one failed
            // leg cannot terminate the lane under its siblings; the traversal's own fail-fast still
            // comes from the leg's subscriber, exactly as in the unbounded overload.
            var admissions = new Subject<IObservable<Unit>>();
            var admit = Subject.Synchronize(admissions);
            var lane = admissions.MergeBounded(maxConcurrentDeletes).Subscribe(_ => { });
            // Set the moment the traversal FAILS or is disposed, before anything is torn down.
            // Tearing down cancels the running legs, and a cancelled leg frees its slot at once —
            // so without this the lane would admit the next queued leg in the middle of the
            // teardown, before that leg's own subscriber had been disposed.
            var stopped = new BooleanDisposable();

            IObservable<string> Admitted(string path) => Observable.Create<string>(legObserver =>
            {
                var released = new BooleanDisposable();
                var cancel = new AsyncSubject<Unit>();
                admit.OnNext(Observable
                    .Defer(() => stopped.IsDisposed || released.IsDisposed
                        // Its subscriber is gone (a sibling failed, or the traversal was
                        // disposed) before a slot came free: the leg never starts.
                        ? Observable.Empty<string>()
                        : deleteOne(path))
                    .TakeUntil(cancel)
                    .Materialize()
                    .Do(n => n.Accept(legObserver))
                    .Select(_ => Unit.Default));
                return Disposable.Create(() =>
                {
                    released.Dispose();
                    cancel.OnNext(Unit.Default);
                    cancel.OnCompleted();
                });
            });

            var traversal = DeleteSubtree(rootPath, descendantPaths, Admitted)
                .Do(_ => { }, _ => stopped.Dispose())
                .Subscribe(observer);
            // Disposed in this order: stop admitting, then tear the traversal and the lane down.
            return new CompositeDisposable(
                stopped, traversal, lane, Disposable.Create(admissions.OnCompleted));
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
