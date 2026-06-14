namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Produces a short natural-language summary of a document's extracted text. Owned by the
/// indexing core as a thin abstraction so the core takes NO heavy AI dependency — the real,
/// <c>IChatClient</c>-backed implementation lives in a hosting/AI project and is out of scope
/// here; tests supply a deterministic fake.
/// </summary>
/// <remarks>
/// Reactive contract (no <c>async</c>/<c>await</c>/<c>Task</c>): the real implementation wraps a
/// single <c>IChatClient</c> chat-completion call through <c>IIoPool.Invoke(ct =&gt; chat...(ct))</c>
/// — the summarize call is ONE I/O-pool leaf that takes its OWN slot. The orchestration in
/// <see cref="ContentIndexingService"/> holds no slot while it runs; it composes this observable
/// with <c>.SelectMany</c> and never awaits it. Emits a single summary string then completes.
/// </remarks>
public interface ISummarizer
{
    /// <summary>
    /// Summarizes <paramref name="text"/> (the file's extracted document text). <paramref name="fileName"/>
    /// is supplied as a prompt hint (title / format cue). Emits exactly one summary string then
    /// completes. Composed (never awaited) by <see cref="ContentIndexingService"/>; called at most
    /// ONCE per document, never per chunk.
    /// </summary>
    IObservable<string> Summarize(string text, string fileName);
}
