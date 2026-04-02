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
/// Results stream as IAsyncEnumerable so fast local results arrive first.
/// </summary>
public interface IChatCompletionOrchestrator
{
    /// <summary>
    /// Streams completion batches as they become available.
    /// Batches arrive in order of readiness (fastest first), each with a priority for sorting.
    /// The caller should merge items from all batches, sorting by CategoryPriority then item Priority.
    /// </summary>
    /// <param name="query">The raw query string including @ prefix (e.g., "@/", "@MyFile", "@/ACME/art").</param>
    /// <param name="currentNamespace">The current node namespace from INavigationService.</param>
    /// <param name="ct">Cancellation token — cancelled when user types more.</param>
    IAsyncEnumerable<CompletionBatch> GetCompletionsAsync(
        string query,
        string? currentNamespace,
        CancellationToken ct = default);
}
