#nullable enable

using System.Reactive.Linq;

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Interface for providers that contribute autocomplete items.
/// Providers are registered in hub DI and aggregated when handling AutocompleteRequest.
/// <para>
/// Observable-first contract: implementations return <see cref="IObservable{AutocompleteItem}"/>
/// so the entire autocomplete chain composes through Rx (Merge, ScanTopN, …) without
/// bridging to <c>Task</c>. Only the innermost provider — where the implementation
/// touches an external system (mesh query, file system, model registry) — is allowed
/// to use <c>await</c>, typically via <c>Observable.Create</c> with an inner
/// <c>await foreach</c> that pumps results into <c>observer.OnNext</c>.
/// </para>
/// </summary>
public interface IAutocompleteProvider
{
    /// <summary>
    /// Gets autocomplete items from this provider as an observable stream.
    /// Each emission is one item; the observable completes when the provider
    /// has nothing more to emit. Errors are reported via <c>OnError</c>.
    /// </summary>
    /// <param name="query">The search query (text being typed after the prefix).</param>
    /// <param name="contextPath">Optional context path for proximity-based ordering.</param>
    IObservable<AutocompleteItem> GetItems(string query, string? contextPath = null);

    /// <summary>
    /// Optional: the UCR prefix this provider handles (e.g., "content", "data", "schema").
    /// Generic providers (mesh nodes, unified references) return null.
    /// Specialized providers return their prefix so generic providers can skip
    /// queries that should be handled exclusively by them.
    /// Aggregated by <see cref="IAutocompletePrefixRegistry"/> and exposed via the prefix/ UCR.
    /// </summary>
    string? Prefix => null;
}

/// <summary>
/// Helpers for building <see cref="IAutocompleteProvider"/> implementations whose
/// underlying source is <see cref="IAsyncEnumerable{T}"/> — the canonical bridge
/// from the storage-layer "innermost <c>await</c>" to the observable contract.
/// </summary>
public static class AutocompleteProviderObservable
{
    /// <summary>
    /// Wraps an async-iterator factory as an <see cref="IObservable{AutocompleteItem}"/>.
    /// The factory is invoked for each subscriber on a background continuation; items
    /// flow through <c>OnNext</c>; the observable completes when the iterator finishes
    /// (or fires <c>OnError</c> if it throws). Subscribing late or unsubscribing early
    /// works naturally because the iteration runs against the provided cancellation
    /// token tied to the subscription.
    /// </summary>
    public static IObservable<AutocompleteItem> FromAsyncEnumerable(
        Func<CancellationToken, IAsyncEnumerable<AutocompleteItem>> factory) =>
        Observable.Create<AutocompleteItem>(async (observer, ct) =>
        {
            try
            {
                await foreach (var item in factory(ct).WithCancellation(ct))
                    observer.OnNext(item);
                observer.OnCompleted();
            }
            catch (OperationCanceledException)
            {
                observer.OnCompleted();
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
            }
        });
}
