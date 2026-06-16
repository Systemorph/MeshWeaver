#nullable enable

using System.Collections.Immutable;
using System.Reactive.Linq;

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Shared building blocks for the progressive, accumulating, score-sorted autocomplete SNAPSHOT
/// model. Every <see cref="IAutocompleteProvider.GetItems"/> emits an
/// <see cref="IReadOnlyCollection{T}"/> of <see cref="AutocompleteItem"/> — the provider's current
/// best list, sorted by <see cref="AutocompleteItem.Priority"/> descending. The aggregator
/// CombineLatest's the providers' snapshot streams and merges them, so the first snapshot appears as
/// soon as the FIRST provider returns and refines as the rest arrive — never waiting for the slowest.
/// <para>
/// This is the <see cref="AutocompleteItem"/> analogue of the QueryResult-side shape already used by
/// <c>MeshQuery.Autocomplete</c> / <c>MeshQuery.MergeAutocompleteSnapshots</c>. Pure
/// <c>System.Reactive</c> — no dependency on the hub assembly's ScanTopN.
/// </para>
/// </summary>
public static class AutocompleteSnapshots
{
    /// <summary>
    /// The empty snapshot — the seed every provider/aggregator starts from. A provider must emit at
    /// least this; it must NEVER return <see cref="Observable.Empty{TResult}()"/>, which would stall the
    /// aggregator's <see cref="Observable.CombineLatest{TSource}(System.Collections.Generic.IEnumerable{IObservable{TSource}})"/>
    /// (CombineLatest only emits once EVERY source has produced at least one value).
    /// </summary>
    public static readonly IReadOnlyCollection<AutocompleteItem> Empty = Array.Empty<AutocompleteItem>();

    /// <summary>
    /// Single source of truth for autocomplete ordering: higher <see cref="AutocompleteItem.Priority"/>
    /// first, then by <see cref="AutocompleteItem.Label"/> (case-insensitive) for a stable tiebreak.
    /// </summary>
    public static readonly IComparer<AutocompleteItem> ByPriorityDescending =
        Comparer<AutocompleteItem>.Create((a, b) =>
        {
            var byPriority = b.Priority.CompareTo(a.Priority);
            return byPriority != 0
                ? byPriority
                : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>
    /// Universal per-provider adapter: turn a provider's item-producing observable into a progressive
    /// accumulating score-sorted snapshot stream. Each incoming item is deduped by
    /// <see cref="AutocompleteItem.InsertText"/>, folded into the running set, re-sorted by priority,
    /// and capped at <paramref name="cap"/>. Seeded with an empty snapshot so a downstream
    /// CombineLatest fires immediately. Used by I/O / progressive providers; pure in-memory providers
    /// can instead return a single <c>Observable.Return(snapshot)</c>.
    /// </summary>
    public static IObservable<IReadOnlyCollection<AutocompleteItem>> FromItems(
        IObservable<AutocompleteItem> items, int cap)
        => items
            .Distinct(i => i.InsertText)
            .Scan(ImmutableList<AutocompleteItem>.Empty, (acc, item) =>
            {
                var grown = acc.Add(item).Sort(ByPriorityDescending);
                return grown.Count > cap ? grown.GetRange(0, cap) : grown;
            })
            .Select(list => (IReadOnlyCollection<AutocompleteItem>)list)
            .StartWith(Empty);

    /// <summary>
    /// Merge a set of provider snapshots into one: dedup by <see cref="AutocompleteItem.InsertText"/>
    /// (keep the highest <see cref="AutocompleteItem.Priority"/>), sort by priority descending, take
    /// <paramref name="limit"/>. Structurally identical to <c>MeshQuery.MergeAutocompleteSnapshots</c>
    /// (which dedups by Path) so the two stay in step.
    /// </summary>
    public static IReadOnlyCollection<AutocompleteItem> MergeSnapshots(
        IEnumerable<IReadOnlyCollection<AutocompleteItem>> snapshots, int limit)
    {
        var byInsert = new Dictionary<string, AutocompleteItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
            foreach (var item in snapshot)
            {
                if (string.IsNullOrEmpty(item.InsertText)) continue;
                if (byInsert.TryGetValue(item.InsertText, out var existing) && existing.Priority >= item.Priority)
                    continue;
                byInsert[item.InsertText] = item;
            }
        return byInsert.Values
            .OrderByDescending(i => i.Priority)
            .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// The aggregator core: concurrently subscribe to every provider snapshot stream (each seeded with
    /// an empty snapshot), and emit a merged score-sorted snapshot every time ANY provider advances.
    /// Mirrors <c>MeshQuery.Autocomplete</c>'s <c>CombineLatest(... .StartWith(empty))</c> — the merged
    /// snapshot appears as soon as the first provider returns and never waits for the slowest.
    /// </summary>
    public static IObservable<IReadOnlyCollection<AutocompleteItem>> Combine(
        IEnumerable<IObservable<IReadOnlyCollection<AutocompleteItem>>> sources, int limit)
    {
        var seeded = sources.Select(s => s.StartWith(Empty)).ToArray();
        if (seeded.Length == 0)
            return Observable.Return(Empty);
        return Observable.CombineLatest(seeded)
            .Select(snaps => MergeSnapshots(snaps, limit));
    }
}
