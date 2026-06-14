using System.Reactive.Linq;
using MeshWeaver.ContentCollections.Indexing;

namespace MeshWeaver.ContentCollections.Indexing.Test;

/// <summary>
/// Deterministic in-process <see cref="ISummarizer"/> for tests: the "summary" is the literal
/// prefix <c>"SUMMARY: "</c> followed by the first <see cref="HeadChars"/> characters of the text.
/// No network, no AI dependency — stable across runs, and lets a test assert the exact summary
/// that should have been written to the document. Also counts calls so a test can prove the
/// summarizer ran ONCE per document (never per chunk) and not at all when the file is skipped.
/// </summary>
public sealed class FakeSummarizer : ISummarizer
{
    private const int HeadChars = 40;

    private int _calls;

    /// <summary>Number of times <see cref="Summarize"/> was invoked (subscribed).</summary>
    public int Calls => Volatile.Read(ref _calls);

    public IObservable<string> Summarize(string text, string fileName) =>
        Observable.Defer(() =>
        {
            Interlocked.Increment(ref _calls);
            return Observable.Return(Expected(text));
        });

    /// <summary>The exact summary string this fake produces for <paramref name="text"/>.</summary>
    public static string Expected(string text) =>
        "SUMMARY: " + (text.Length <= HeadChars ? text : text[..HeadChars]);
}
