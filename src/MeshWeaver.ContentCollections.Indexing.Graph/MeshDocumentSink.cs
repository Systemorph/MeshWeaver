using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections.Indexing.Graph;

/// <summary>
/// The framework <see cref="IDocumentSink"/>: writes the per-file <see cref="DocumentInfo"/> as a
/// first-class <c>Document</c> mesh node. The node lives at the deterministic, url-safe path
/// <see cref="DocumentPaths.For"/>, so a re-index of the same file UPDATES the same node rather than
/// forking a duplicate.
///
/// <para>The write goes through the single canonical upsert verb
/// <see cref="CreateOrUpdateNodeRequest"/> (same path <c>NodeCopyHelper</c>/<c>StaticRepoImporter</c>
/// use): the mesh hub creates the node when absent and updates it via
/// <c>workspace.GetMeshNodeStream(path).Update(...)</c> when present, running the full pipeline
/// (prerender, embedding, access). Reactive end-to-end — no <c>async</c>/<c>await</c>; the
/// <c>hub.Observe(...)</c> is composed and the caller's <c>.Subscribe()</c> runs the write. The
/// caller's <c>AccessContext</c> rides through the Subscribe boundary, so the document is written
/// under the indexing user's identity (never as the hub).</para>
/// </summary>
public sealed class MeshDocumentSink(IMessageHub hub) : IDocumentSink
{
    /// <inheritdoc />
    public IObservable<Unit> WriteDocument(DocumentInfo doc)
    {
        var path = DocumentPaths.For(doc.CollectionPath, doc.FilePath);
        var node = MeshNode.FromPath(path) with
        {
            NodeType = DocumentNodeType.NodeType,
            Name = doc.FileName,
            State = MeshNodeState.Active,
            Content = new Document
            {
                Name = doc.FileName,
                Summary = doc.Summary,
                CollectionPath = doc.CollectionPath,
                FilePath = doc.FilePath,
                Mime = doc.Mime,
                SizeBytes = doc.SizeBytes,
                ContentHash = doc.ContentHash,
                ChunkCount = doc.ChunkCount,
                IndexedAt = DateTimeOffset.UtcNow
            }
        };

        // Cold: the side effect runs on Subscribe (driven by ContentIndexingService.WriteDocumentBranch).
        // FirstAsync() completes after the single response; map to Unit per the IDocumentSink contract.
        return hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node))
            .FirstAsync()
            .SelectMany(delivery =>
            {
                var response = delivery.Message;
                // "Node already exists" is SUCCESS for an idempotent upsert — the node is present,
                // which IS the goal (the eventually-consistent exists-check can lag a recent create
                // on a re-index and fall through to CreateNode). Any OTHER error faults the write so
                // the indexing pipeline surfaces it rather than silently dropping the document.
                if (response.Success
                    || (response.Error?.Contains("already exists", StringComparison.OrdinalIgnoreCase) ?? false))
                    return Observable.Return(Unit.Default);

                return Observable.Throw<Unit>(new InvalidOperationException(
                    $"Failed to write Document node at '{path}': {response.Error}"));
            });
    }
}
