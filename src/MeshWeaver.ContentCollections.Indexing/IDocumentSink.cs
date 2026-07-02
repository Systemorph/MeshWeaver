using System.Reactive;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Sink for the per-file <see cref="DocumentInfo"/> produced when a changed file is indexed.
/// A thin abstraction owned by the indexing core; the real implementation — which creates or
/// updates a framework-generic <c>Document</c> NodeType instance via the workspace — lives in a
/// hosting project and is OUT of scope here. Tests supply a fake that records the last
/// <see cref="DocumentInfo"/> written.
/// </summary>
/// <remarks>
/// Reactive contract (no <c>async</c>/<c>await</c>/<c>Task</c>): the real implementation will
/// create/update a <c>Document</c> mesh node (Name = <see cref="DocumentInfo.FileName"/>, body =
/// the <see cref="DocumentInfo.Summary"/>, plus the source path and metadata —
/// <see cref="DocumentInfo.Mime"/>, <see cref="DocumentInfo.SizeBytes"/>,
/// <see cref="DocumentInfo.ContentHash"/>, <see cref="DocumentInfo.ChunkCount"/>) through
/// <c>workspace.GetMeshNodeStream(path).Update(...)</c> (or <c>CreateOrUpdateNodeRequest</c> for a
/// new node), and is therefore composed — never awaited. Cold: subscribe to run. Emits one
/// <see cref="Unit"/> on success then completes; errors surface via <c>OnError</c>.
/// </remarks>
public interface IDocumentSink
{
    /// <summary>
    /// Creates or updates the <c>Document</c> node for <paramref name="doc"/>. Cold observable —
    /// the write runs on subscribe. Composed (never awaited) by <see cref="ContentIndexingService"/>.
    /// </summary>
    IObservable<Unit> WriteDocument(DocumentInfo doc);

    /// <summary>
    /// Whether a <c>Document</c> already exists for <c>(collectionPath, filePath)</c>. Drives the
    /// hash-gate HEAL in <see cref="ContentIndexingService.IndexFile"/>: a file whose chunks are
    /// up to date may still be missing its per-file Document (indexed before the document branch
    /// was wired, or its write failed) — the gate skips re-chunking but must not skip a missing
    /// Document forever. Cold; emits one value then completes. Indeterminate probes should lean
    /// towards <c>false</c>: <see cref="WriteDocument"/> is an idempotent upsert, so a spurious
    /// heal is harmless while a spurious "exists" wedges the missing Document permanently.
    /// </summary>
    IObservable<bool> DocumentExists(string collectionPath, string filePath);
}
