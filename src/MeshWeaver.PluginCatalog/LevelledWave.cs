using System.Reactive.Linq;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Runs work over items LEVEL BY LEVEL — every item of level <c>n</c> is decided before the first
/// item of level <c>n+1</c> is subscribed (a level is a barrier) — and within a level up to
/// <c>degree</c> items at once. The shape of the default install's module phase
/// (<see cref="PackageDependencyGraph.DependencyLevels"/>): a module's dependencies land first, and
/// modules that do not depend on each other land together.
/// </summary>
internal static class LevelledWave
{
    /// <summary>
    /// Runs <paramref name="work"/> for every item. Cold; each item's work is subscribed only when
    /// it gets a slot, so a per-item timeout starts with the slot, not with the wave. Emits every
    /// result once, level by level (within a level in completion order), then completes.
    /// </summary>
    /// <param name="items">The items, in any order.</param>
    /// <param name="levelOf">An item's level; lower runs first.</param>
    /// <param name="degree">The most items of one level in flight at once (≥ 1).</param>
    /// <param name="work">The cold work for one item.</param>
    public static IObservable<TResult> Run<TItem, TResult>(
        IEnumerable<TItem> items, Func<TItem, int> levelOf, int degree, Func<TItem, IObservable<TResult>> work)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(degree, 1);
        return items
            .GroupBy(levelOf)
            .OrderBy(level => level.Key)
            .Select(level => level.Select(item => Observable.Defer(() => work(item))).Merge(degree))
            .ToObservable()
            .Concat();
    }
}
