using System.Reactive.Linq;
using MeshWeaver.Data.Completion;
using MeshWeaver.Reactive;

namespace MeshWeaver.AI.Completion;

/// <summary>
/// Reactive entry point for autocomplete consumers in the same hub as the providers.
/// Returns a stream of top-N snapshots that grows as each <see cref="IAutocompleteProvider"/>
/// finishes producing items — fast local providers emit early, remote ones merge in later,
/// the snapshot keeps refining until everything completes.
///
/// <para>
/// Consumers (Blazor components, layout areas, plugins) subscribe and receive each snapshot;
/// no <c>Task</c>, no <c>await</c>, no <c>Hub.AwaitResponse</c>. For cross-hub autocomplete,
/// the existing <see cref="AutocompleteRequest"/>/<see cref="AutocompleteResponse"/>
/// message pair still applies — that handler aggregates with <c>LastOrDefaultAsync</c> and
/// posts the final snapshot.
/// </para>
/// </summary>
public interface IAutocompleteStreamProvider
{
    /// <summary>
    /// Subscribe to streaming autocomplete results for <paramref name="query"/>. Each
    /// emission is a top-N snapshot ordered by <see cref="AutocompleteItem.Priority"/>
    /// (higher first). The first emission is an empty snapshot (so consumers can render
    /// their initial empty state). Completes when every registered provider's
    /// <c>GetItemsAsync</c> stream has finished.
    /// </summary>
    IObservable<IReadOnlyList<AutocompleteItem>> Stream(string query, string? contextPath);
}

/// <summary>
/// Default <see cref="IAutocompleteStreamProvider"/>. Merges every registered
/// <see cref="IAutocompleteProvider"/>'s <c>IAsyncEnumerable</c> via
/// <see cref="ObservableTopNExtensions.ToObservableSequence{T}(IAsyncEnumerable{T})"/>
/// and folds the merged stream through
/// <see cref="ObservableTopNExtensions.ScanTopN{T}(IObservable{T},int,IComparer{T})"/>.
/// </summary>
public sealed class AutocompleteStreamProvider(IEnumerable<IAutocompleteProvider> providers, int topN = 50)
    : IAutocompleteStreamProvider
{
    /// <summary>
    /// Higher <see cref="AutocompleteItem.Priority"/> = better. Sort descending so the
    /// top-N snapshot has the best item at index 0.
    /// </summary>
    public static readonly IComparer<AutocompleteItem> ByPriorityDescending =
        Comparer<AutocompleteItem>.Create((a, b) => b.Priority.CompareTo(a.Priority));

    public IObservable<IReadOnlyList<AutocompleteItem>> Stream(string query, string? contextPath)
    {
        var snapshot = providers
            .Select(p => p.GetItemsAsync(query, contextPath, default)
                .ToObservableSequence()
                .Catch(Observable.Empty<AutocompleteItem>()))
            .Merge()
            .ScanTopN(topN, ByPriorityDescending);
        return snapshot;
    }
}
