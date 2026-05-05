#nullable enable

namespace MeshWeaver.Data.Completion;

/// <summary>
/// A batch of autocomplete items from a single provider group.
/// Groups arrive progressively as providers finish — fast local results first, remote later.
/// </summary>
/// <param name="Category">Display category (e.g., "Nearby", "Partitions", "Global").</param>
/// <param name="CategoryPriority">Sort priority — higher values appear first in the list.</param>
/// <param name="Items">The autocomplete items in this batch.</param>
public record CompletionBatch(
    string Category,
    int CategoryPriority,
    IReadOnlyList<AutocompleteItem> Items);

/// <summary>
/// Orchestrates chat autocomplete across multiple provider groups:
/// 1) Current node providers (content, data, layout areas) — highest priority
/// 2) Partition list (when typing @/)
/// 3) Partition drill-down (when a partition is selected)
/// 4) Global fan-out across all partitions — lowest priority
///
/// Returns an <see cref="IObservable{T}"/> so consumers can detect stream completion
/// (e.g., to hide a "loading" spinner once all providers have finished).
/// </summary>
public interface IChatCompletionOrchestrator
{
    /// <summary>
    /// Streams completion batches as they become available.
    /// Batches arrive in order of readiness (fastest first), each with a priority for sorting.
    /// The caller should merge items from all batches, sorting by CategoryPriority then item Priority.
    ///
    /// <para>Subscribers receive each <see cref="CompletionBatch"/> via <c>OnNext</c> as a
    /// producer finishes. <c>OnCompleted</c> fires when all producers (current-node hub,
    /// subtree query, partition fan-out, broadening) have completed — the chat UI uses this
    /// to hide its loading indicator.</para>
    /// </summary>
    /// <param name="query">The raw query string including @ prefix (e.g., "@/", "@MyFile", "@/ACME/art").</param>
    /// <param name="currentNamespace">The current node namespace from INavigationService.</param>
    IObservable<CompletionBatch> GetCompletions(string query, string? currentNamespace);
}
