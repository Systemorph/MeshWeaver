using System.Reactive.Linq;
using MeshWeaver.Data.Completion;

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
    /// <c>GetItems</c> observable has completed.
    /// </summary>
    IObservable<IReadOnlyList<AutocompleteItem>> Stream(string query, string? contextPath);
}

/// <summary>
/// Default <see cref="IAutocompleteStreamProvider"/>. CombineLatest's every registered
/// <see cref="IAutocompleteProvider.GetItems"/> snapshot stream and merges them through
/// <see cref="AutocompleteSnapshots.Combine"/>, so the merged top-N snapshot appears as
/// soon as the first provider returns and refines as the rest arrive.
/// </summary>
public sealed class AutocompleteStreamProvider(IEnumerable<IAutocompleteProvider> providers, int topN = 50)
    : IAutocompleteStreamProvider
{
    /// <inheritdoc />
    public IObservable<IReadOnlyList<AutocompleteItem>> Stream(string query, string? contextPath)
    {
        return AutocompleteSnapshots
            .Combine(
                providers.Select(p => p.GetItems(query, contextPath)
                    .Catch(Observable.Return(AutocompleteSnapshots.Empty))),
                topN)
            .Select(snapshot => (IReadOnlyList<AutocompleteItem>)snapshot.ToList());
    }
}
