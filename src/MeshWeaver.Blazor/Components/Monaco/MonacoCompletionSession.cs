namespace MeshWeaver.Blazor.Components.Monaco;

/// <summary>
/// Per-editor state machine behind the Monaco async-completion glue
/// (<c>MonacoEditorView.GetAsyncCompletions</c>). One instance lives per editor; each distinct
/// query opens exactly one subscription to the consumer's completion observable, and every
/// snapshot that subscription emits is pushed to JS <b>stamped with the query it answers</b>.
///
/// <para>That stamp is the contract issue #542 hangs on: the JS provider buffers pushed
/// snapshots (<c>state._pendingCompletionItems</c> in MonacoEditorView.razor.js) and, on the
/// re-triggered suggest request, consumes the buffer <i>instead of fetching</i>. When the push
/// was unkeyed, a buffer produced for an earlier trigger token (e.g. <c>@</c>) was served as
/// the answer for the now-current one (e.g. <c>@Zebra</c>) — wrong first results — while the
/// fetch for the real query was skipped, so only a manual re-trigger recovered. The stamp lets
/// the provider consume a buffered snapshot only when it answers the query currently being
/// completed, and discard-and-fetch otherwise.</para>
/// </summary>
/// <param name="subscribe">Opens the completion stream for a query (the component's
/// <c>CompletionCallback</c>). Evaluated per query change, so a swapped-in callback is picked
/// up on the next query.</param>
/// <param name="push">Delivers a snapshot to the suggest widget, keyed by the query the
/// snapshot answers. Invoked synchronously from the subscription's OnNext.</param>
/// <param name="onError">Receives the failing query and the error from the stream or from a
/// synchronously-throwing subscribe.</param>
public sealed class MonacoCompletionSession(
    Func<string, IObservable<IReadOnlyList<CompletionItem>>> subscribe,
    Action<string, CompletionItem[]> push,
    Action<string, Exception> onError) : IDisposable
{
    private readonly object gate = new();
    private CompletionItem[] currentCompletions = [];
    private string? currentQuery;
    private IDisposable? activeSubscription;

    /// <summary>
    /// Returns the latest snapshot for <paramref name="query"/> synchronously. A query change
    /// disposes the previous subscription, resets the snapshot to empty, and subscribes the
    /// stream for the new query — whose emissions arrive via <c>push</c>, each stamped with
    /// this query. A repeated query returns the live snapshot without resubscribing (the
    /// existing subscription keeps pushing). An emission from a superseded subscription that
    /// is still in flight when the query changes is dropped here (never clobbers the new
    /// query's snapshot) — and its query stamp would not match the current trigger anyway.
    /// </summary>
    public CompletionItem[] GetCompletions(string query)
    {
        IObservable<IReadOnlyList<CompletionItem>>? stream = null;
        lock (gate)
        {
            if (string.Equals(currentQuery, query, StringComparison.Ordinal))
                return currentCompletions;

            currentQuery = query;
            activeSubscription?.Dispose();
            activeSubscription = null;
            currentCompletions = [];
        }

        try
        {
            stream = subscribe(query);
        }
        catch (Exception ex)
        {
            onError(query, ex);
        }

        if (stream is null)
            return [];

        IDisposable? subscription = null;
        try
        {
            subscription = stream.Subscribe(
                snapshot =>
                {
                    var arr = snapshot as CompletionItem[] ?? snapshot.ToArray();
                    lock (gate)
                    {
                        // Superseded mid-flight: a newer query owns the session now.
                        if (!string.Equals(currentQuery, query, StringComparison.Ordinal))
                            return;
                        currentCompletions = arr;
                    }
                    push(query, arr);
                },
                ex => onError(query, ex));
        }
        catch (Exception ex)
        {
            onError(query, ex);
        }

        lock (gate)
        {
            if (string.Equals(currentQuery, query, StringComparison.Ordinal))
            {
                activeSubscription = subscription;
                return currentCompletions;
            }
        }

        // The query moved on while we were subscribing — this subscription lost the session.
        subscription?.Dispose();
        return [];
    }

    /// <summary>Disposes the active completion subscription, if any.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            activeSubscription?.Dispose();
            activeSubscription = null;
        }
    }
}
