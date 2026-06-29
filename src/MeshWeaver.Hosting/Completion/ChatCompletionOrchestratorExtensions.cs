using MeshWeaver.Reactive;

// In Data.Completion namespace so callers that already `using MeshWeaver.Data.Completion;`
// pick up the extension without an extra import. The implementation lives in MeshWeaver.Hosting
// because that's where the IAsyncEnumerable bridge (ToAsyncEnumerableSequence) is available.
namespace MeshWeaver.Data.Completion;

/// <summary>
/// Backwards-compat bridge for callers that want the orchestrator's stream as
/// <see cref="IAsyncEnumerable{T}"/>. New code should subscribe to the IObservable
/// surface directly (see <see cref="IChatCompletionOrchestrator.GetCompletions"/>).
/// </summary>
public static class ChatCompletionOrchestratorExtensions
{
    /// <summary>
    /// Adapts the orchestrator's observable completion stream to an <see cref="IAsyncEnumerable{T}"/>
    /// of completion batches for backwards-compatible callers.
    /// </summary>
    /// <param name="orchestrator">The orchestrator producing completions.</param>
    /// <param name="query">The partial input to complete.</param>
    /// <param name="currentNamespace">The namespace context for resolving relative completions, or <c>null</c>.</param>
    /// <param name="ct">Token used to cancel enumeration.</param>
    /// <returns>An async sequence of completion batches.</returns>
    public static IAsyncEnumerable<CompletionBatch> GetCompletionsAsync(
        this IChatCompletionOrchestrator orchestrator,
        string query,
        string? currentNamespace,
        CancellationToken ct = default)
        => orchestrator.GetCompletions(query, currentNamespace).ToAsyncEnumerableSequence(ct);
}
