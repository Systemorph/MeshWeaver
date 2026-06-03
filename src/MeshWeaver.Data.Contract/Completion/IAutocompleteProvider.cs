#nullable enable

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Interface for providers that contribute autocomplete items.
/// Providers are registered in hub DI and aggregated when handling AutocompleteRequest.
/// <para>
/// Observable-first contract: implementations return <see cref="IObservable{AutocompleteItem}"/>
/// so the entire autocomplete chain composes through Rx (Merge, ScanTopN, …) without
/// bridging to <c>Task</c>. The innermost provider — where the implementation touches an
/// external system (mesh query, file system, model registry) — bridges its async/IAsyncEnumerable
/// leaf through the shared <c>IIoPool</c> (<c>pool.Run</c> / <c>pool.RunStream</c>), never a bare
/// <c>Observable.FromAsync</c>/<c>Observable.Create(await foreach)</c>, which would deadlock under
/// a blocking subscriber. See <c>Doc/Architecture/AsynchronousCalls.md</c>.
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
