using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.ContentCollections.Indexing;

namespace MeshWeaver.ContentCollections.Indexing.Test;

/// <summary>
/// In-process <see cref="IDocumentSink"/> for tests: records the last <see cref="DocumentInfo"/>
/// written and counts writes, so a test can assert WHAT was written (summary, file name, hash,
/// chunk count) and that a write happened exactly once on change / not at all when skipped.
/// </summary>
public sealed class FakeDocumentSink : IDocumentSink
{
    private int _writes;

    /// <summary>The most recent <see cref="DocumentInfo"/> handed to <see cref="WriteDocument"/>, or null if none.</summary>
    public DocumentInfo? LastDocument { get; private set; }

    /// <summary>Number of times <see cref="WriteDocument"/> was invoked (subscribed).</summary>
    public int Writes => Volatile.Read(ref _writes);

    public IObservable<Unit> WriteDocument(DocumentInfo doc) =>
        Observable.Defer(() =>
        {
            LastDocument = doc;
            Interlocked.Increment(ref _writes);
            return Observable.Return(Unit.Default);
        });
}
