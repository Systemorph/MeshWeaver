#nullable enable

namespace MeshWeaver.Data.Completion;

/// <summary>
/// Interface for providers that contribute autocomplete items.
/// Providers are registered in hub DI and aggregated when handling AutocompleteRequest.
/// <para>
/// SNAPSHOT contract: each provider returns an <see cref="IObservable{T}"/> of
/// <see cref="IReadOnlyCollection{T}"/> snapshots — every emission is the provider's CURRENT best
/// list, sorted by <see cref="AutocompleteItem.Priority"/> descending. The aggregator
/// CombineLatest's the providers' snapshot streams and merges them (see
/// <see cref="AutocompleteSnapshots"/>), so the first merged snapshot appears as soon as the FIRST
/// provider returns and refines as the rest arrive — it never waits for the slowest. Build a
/// snapshot stream from item-producing logic with <see cref="AutocompleteSnapshots.FromItems"/>; pure
/// in-memory providers return a single <c>Observable.Return(snapshot)</c>.
/// </para>
/// <para>
/// The innermost provider — where the implementation touches an external system (mesh query, file
/// system, model registry) — bridges its async/IAsyncEnumerable leaf through the shared
/// <c>IIoPool</c> (<c>pool.Run</c> / <c>pool.RunStream</c>), never a bare
/// <c>Observable.FromAsync</c>/<c>Observable.Create(await foreach)</c>. See
/// <c>Doc/Architecture/AsynchronousCalls.md</c>.
/// </para>
/// </summary>
public interface IAutocompleteProvider
{
    /// <summary>
    /// Gets autocomplete suggestions from this provider as a progressive snapshot stream. Each
    /// emission is the provider's current best <see cref="IReadOnlyCollection{T}"/>, sorted by
    /// <see cref="AutocompleteItem.Priority"/> descending; the stream completes when the provider has
    /// settled. A provider must emit at least an empty snapshot
    /// (<see cref="AutocompleteSnapshots.Empty"/>) — NEVER <c>Observable.Empty</c>, which stalls the
    /// aggregator's CombineLatest. Errors are reported via <c>OnError</c>.
    /// </summary>
    /// <param name="query">The search query (text being typed after the prefix).</param>
    /// <param name="contextPath">Optional context path for proximity-based ordering.</param>
    IObservable<IReadOnlyCollection<AutocompleteItem>> GetItems(string query, string? contextPath = null);

    /// <summary>
    /// Optional: the UCR prefix this provider handles (e.g., "content", "data", "schema").
    /// Generic providers (mesh nodes, unified references) return null.
    /// Specialized providers return their prefix so generic providers can skip
    /// queries that should be handled exclusively by them.
    /// Aggregated by <see cref="IAutocompletePrefixRegistry"/> and exposed via the prefix/ UCR.
    /// </summary>
    string? Prefix => null;
}
