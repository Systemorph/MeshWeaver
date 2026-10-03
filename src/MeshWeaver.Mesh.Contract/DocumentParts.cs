using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reactive.Linq;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// Where a <c>Document</c>'s PARTS live — the one definition of the path, segment, table and node
/// type of the per-chunk satellites, shared by the satellite table mapping
/// (<see cref="SatelliteTableMapping.Defaults"/>), the writers in the indexing module and every
/// reader.
///
/// <para><b>The shape.</b> A document is ONE logical node at
/// <c>DocumentPaths.For(collectionPath, filePath)</c> = <c>{collection}/_Documents/{slug}</c>. Its
/// text is split into 1000-character windows overlapping by 150 (the same windows the content
/// chunk index uses — <c>chunkIndex</c> N of a file is part N of its document), and every window is
/// a <see cref="DocumentPart"/> node at <c>{documentPath}/_DocumentPart/{index:D6}</c>, stored in
/// the <c>document_parts</c> satellite table of the partition that owns the document. A node
/// attached to one part — an annotation — sits at <c>{partPath}/_PartAnnotation/{id}</c> in
/// <c>document_part_annotations</c>. The ORIGINAL stays in its content collection; parts carry
/// their offset into it and the hash of their own text.</para>
///
/// <para>Full reference: <c>Doc/Architecture/DocumentParts</c>.</para>
/// </summary>
public static class DocumentPartPaths
{
    /// <summary>The node type of the logical document (registered by the content-indexing module).</summary>
    public const string DocumentNodeType = "Document";

    /// <summary>The satellite segment the parts of a document live under.</summary>
    public const string PartSegment = "_DocumentPart";

    /// <summary>The satellite table the parts live in, inside the owning partition's schema.</summary>
    public const string PartTable = "document_parts";

    /// <summary>The node type of one part.</summary>
    public const string PartNodeType = "DocumentPart";

    /// <summary>The satellite segment of the nodes attached to ONE part.</summary>
    public const string AnnotationSegment = "_PartAnnotation";

    /// <summary>The satellite table of the nodes attached to parts.</summary>
    public const string AnnotationTable = "document_part_annotations";

    /// <summary>The node type of an annotation on a part.</summary>
    public const string AnnotationNodeType = "DocumentPartAnnotation";

    /// <summary>Characters per part — the content chunk index's window size.</summary>
    public const int DefaultChunkSize = 1000;

    /// <summary>Characters two consecutive parts share — the content chunk index's overlap.</summary>
    public const int DefaultChunkOverlap = 150;

    /// <summary>The namespace holding the parts of <paramref name="documentPath"/>.</summary>
    /// <param name="documentPath">The logical document's path.</param>
    /// <returns><c>{documentPath}/_DocumentPart</c>.</returns>
    public static string PartNamespace(string documentPath) =>
        $"{Trim(documentPath)}/{PartSegment}";

    /// <summary>The id of part <paramref name="index"/>: zero-padded so ids sort in part order.</summary>
    /// <param name="index">Zero-based part (chunk) index.</param>
    /// <returns>The six-digit id, e.g. <c>000042</c>.</returns>
    public static string PartId(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return index.ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>The path of part <paramref name="index"/> of <paramref name="documentPath"/>.</summary>
    /// <param name="documentPath">The logical document's path.</param>
    /// <param name="index">Zero-based part (chunk) index.</param>
    /// <returns><c>{documentPath}/_DocumentPart/{index:D6}</c>.</returns>
    public static string PartPath(string documentPath, int index) =>
        $"{PartNamespace(documentPath)}/{PartId(index)}";

    /// <summary>Parses a part id back into its index; false for anything that is not a part id.</summary>
    /// <param name="id">A node id from the part namespace.</param>
    /// <param name="index">The parsed index.</param>
    /// <returns>True when <paramref name="id"/> is a part id.</returns>
    public static bool TryParsePartIndex(string? id, out int index) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out index);

    /// <summary>The namespace holding the annotations attached to one part.</summary>
    /// <param name="partPath">The part's path.</param>
    /// <returns><c>{partPath}/_PartAnnotation</c>.</returns>
    public static string AnnotationNamespace(string partPath) =>
        $"{Trim(partPath)}/{AnnotationSegment}";

    /// <summary>
    /// Why the parts of <paramref name="documentPath"/> would NOT land in <see cref="PartTable"/>, or
    /// null when they would.
    ///
    /// <para>🚨 A storage adapter places a satellite by the LONGEST mapped segment anywhere in its path
    /// (<c>PartitionDefinition.ResolveTable</c>). <see cref="PartSegment"/> outranks every satellite a
    /// document normally hangs under, but a document filed under an equally long or longer segment
    /// (<c>_ThreadMessage</c>, <c>_Notification</c>, <c>_UserActivity</c>) would have its parts
    /// written into THAT table — written, acknowledged and never found by the part reads. The writers
    /// refuse such a document up front instead.</para>
    /// </summary>
    /// <param name="documentPath">The logical document's path.</param>
    /// <returns>Null when the placement is correct; otherwise the reason.</returns>
    public static string? PartPlacementProblem(string documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
            return "A document path is required.";
        var segments = PartPath(documentPath, 0).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var winner = SatelliteTableMapping.Defaults
            .Where(m => segments.Contains(m.Segment, StringComparer.Ordinal))
            .OrderByDescending(m => m.Segment.Length)
            .ToArray();
        if (winner.Length == 0 || !string.Equals(winner[0].Table, PartTable, StringComparison.Ordinal))
            return $"The parts of '{documentPath}' would be stored in '{(winner.Length == 0 ? "mesh_nodes" : winner[0].Table)}', "
                   + $"not '{PartTable}': a document must not live under the satellite segment '{(winner.Length == 0 ? "" : winner[0].Segment)}'.";
        var rival = winner.Skip(1).FirstOrDefault(m =>
            m.Segment.Length == winner[0].Segment.Length
            && !string.Equals(m.Table, PartTable, StringComparison.Ordinal));
        return rival is null
            ? null
            : $"The parts of '{documentPath}' tie with the satellite segment '{rival.Segment}' (table '{rival.Table}'); "
              + "file the document outside that satellite.";
    }

    private static string Trim(string path) => (path ?? string.Empty).Trim('/');
}

/// <summary>
/// One part (chunk) of a logical document: the content of a <see cref="DocumentPartPaths.PartNodeType"/>
/// node at <c>{documentPath}/_DocumentPart/{index:D6}</c>. Written ONCE per index, as soon as the
/// window is complete, and idempotent by <c>(document, index)</c> — a retry re-writes the same path
/// with the same text.
/// </summary>
public record DocumentPart
{
    /// <summary>The node id — the six-digit part index.</summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>The logical document this part belongs to.</summary>
    public string DocumentPath { get; init; } = string.Empty;

    /// <summary>Zero-based position of this part in the document — the content index's <c>chunkIndex</c>.</summary>
    public int Index { get; init; }

    /// <summary>Character offset of <see cref="Text"/> in the ORIGINAL.</summary>
    public long Start { get; init; }

    /// <summary>The part's text: a window of the original, overlapping its neighbours.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>SHA-256 (lower hex) of <see cref="Text"/> as UTF-8 — proves which bytes were indexed.</summary>
    public string TextHash { get; init; } = string.Empty;

    /// <summary>The content collection the original lives in (the chunk index key).</summary>
    public string CollectionPath { get; init; } = string.Empty;

    /// <summary>The original's file path within <see cref="CollectionPath"/> (the chunk index key).</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>True when the part's embedding reached the vector index.</summary>
    public bool Indexed { get; init; }

    /// <summary>Why the part is not in the vector index, when <see cref="Indexed"/> is false.</summary>
    public string? IndexError { get; init; }
}

/// <summary>
/// A node attached to one <see cref="DocumentPart"/> — a person's or an agent's note or label on that
/// chunk — at <c>{partPath}/_PartAnnotation/{id}</c>. The bug-fix learning loop reads these: a label
/// says what the chunk shows (<c>root-cause</c>, <c>flake</c>, <c>noise</c>), the text says why.
/// </summary>
public record DocumentPartAnnotation
{
    /// <summary>The node id.</summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>The part this annotation is attached to.</summary>
    public string PartPath { get; init; } = string.Empty;

    /// <summary>The kind of annotation: <c>note</c>, <c>label</c>, <c>finding</c>, …</summary>
    public string Kind { get; init; } = "note";

    /// <summary>A short machine-readable label (e.g. <c>root-cause</c>), when the annotation classifies.</summary>
    public string? Label { get; init; }

    /// <summary>The annotation's text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Optional start of the annotated span, relative to the part's text.</summary>
    public int? SpanStart { get; init; }

    /// <summary>Optional length of the annotated span.</summary>
    public int? SpanLength { get; init; }
}

/// <summary>
/// What a document log is a record OF: where its original lives and how it is cut into parts.
/// Fixed when the document is opened — the windows of a document never change mid-stream.
/// </summary>
/// <param name="CollectionPath">The content collection the original belongs in (<c>{node}/{collection}</c>).</param>
/// <param name="FilePath">The original's file path within the collection.</param>
/// <param name="Name">Display name; defaults to the file name.</param>
/// <param name="ChunkSize">Characters per part; null = <see cref="DocumentPartPaths.DefaultChunkSize"/>.</param>
/// <param name="ChunkOverlap">Characters two consecutive parts share; null = <see cref="DocumentPartPaths.DefaultChunkOverlap"/>.</param>
/// <param name="ProducerStoresOriginal">True when the producer already stored the original (a downloaded file):
/// completion then leaves the collection untouched. False (the default) writes the assembled original into
/// the collection on completion.</param>
/// <remarks>🚨 Every member's default is its CLR default (or a value whose omission means "the default"): the
/// mesh serialiser omits default-valued members, so a positional parameter whose default differs from the
/// CLR default would flip on the way to the document's hub — a <c>false</c> sent would arrive as the
/// constructor's <c>true</c>.</remarks>
public record DocumentLogTarget(
    string CollectionPath,
    string FilePath,
    string? Name = null,
    int? ChunkSize = null,
    int? ChunkOverlap = null,
    bool ProducerStoresOriginal = false)
{
    /// <summary>The logical document's path for this target.</summary>
    public string DocumentPath => DocumentPaths.For(CollectionPath, FilePath);
}

/// <summary>
/// Appends text to a logical document, handled by the document's own hub — the ONE writer of the
/// document, so appends are serialised by the actor that owns it. Complete windows are written as
/// <see cref="DocumentPart"/> nodes and indexed as soon as they are complete.
/// </summary>
public record AppendDocumentTextRequest : IRequest<AppendDocumentTextResponse>
{
    /// <summary>The text to append. May be empty (an open or a completion carries no text).</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// The producer's character offset of <see cref="Text"/> in the original, or null to append at the
    /// end. With an offset the append is idempotent: text the document already holds is skipped, so a
    /// retried or redelivered append never duplicates; a gap (offset past the end) is refused.
    /// </summary>
    public long? Offset { get; init; }

    /// <summary>True when this is the last append: the trailing partial part is written and the document sealed.</summary>
    public bool Complete { get; init; }

    /// <summary>Initialises an un-initialised document; ignored once the document has started.</summary>
    public DocumentLogTarget? Init { get; init; }
}

/// <summary>The document's state after an <see cref="AppendDocumentTextRequest"/>.</summary>
public record AppendDocumentTextResponse
{
    /// <summary>The document's path.</summary>
    public string DocumentPath { get; init; } = string.Empty;

    /// <summary>Characters the document holds.</summary>
    public long Length { get; init; }

    /// <summary>Parts written so far.</summary>
    public int PartCount { get; init; }

    /// <summary>True once the document is sealed.</summary>
    public bool Complete { get; init; }

    /// <summary>Why the append was refused or failed; null on success.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Present in a host's services when the module that WRITES document logs is installed (the content
/// indexing module registers it with the <c>Document</c> node type). A best-effort producer — an agent
/// round's transcript — checks <see cref="DocumentLogExtensions.SupportsDocumentLogs"/> and writes
/// nothing on a host without it, instead of failing a create per round.
/// </summary>
public sealed class DocumentLogSupport;

/// <summary>
/// The producer entry points for a document log — "append log text to document X". Any hub may call
/// them (a job runner, an agent round, the CI log downloader); the document's own hub does the work.
/// </summary>
public static class DocumentLogExtensions
{
    /// <summary>
    /// Opens (creates if absent) the logical document for <paramref name="target"/> and initialises it.
    /// Idempotent: opening an existing document leaves its content untouched. Cold.
    /// </summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="target">Where the original lives and how it is cut.</param>
    /// <returns>The document's path, once it exists and is initialised.</returns>
    public static IObservable<string> OpenDocumentLog(this IMessageHub hub, DocumentLogTarget target)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(target);
        var path = target.DocumentPath;
        if (DocumentPartPaths.PartPlacementProblem(path) is { } problem)
            return Observable.Throw<string>(new ArgumentException(problem, nameof(target)));

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var node = MeshNode.FromPath(path) with
        {
            NodeType = DocumentPartPaths.DocumentNodeType,
            Name = target.Name ?? System.IO.Path.GetFileName(target.FilePath),
            // A document filed under a satellite (a thread's transcript, a job activity's log) belongs
            // to that satellite's owner; under a main node it is its own main node.
            MainNode = SatelliteTableMapping.OwnerOfSatellitePath(path),
            State = MeshNodeState.Active,
        };
        // CreateNode captures the caller's identity at the CALL, so it is built here, eagerly; an
        // existing document is success — open is idempotent.
        var create = meshService.CreateNode(node)
            .Select(_ => true)
            .Catch((Exception ex) => IsAlreadyExists(ex)
                ? Observable.Return(true)
                : Observable.Throw<bool>(ex));
        return create.SelectMany(_ => hub.AppendToDocument(path, new AppendDocumentTextRequest { Init = target }))
            .Select(response => response.Error is null
                ? path
                : throw new InvalidOperationException($"Opening document '{path}' failed: {response.Error}"));
    }

    /// <summary>Appends <paramref name="text"/> to the document at <paramref name="documentPath"/>. Cold.</summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="documentPath">The logical document's path.</param>
    /// <param name="text">The text to append.</param>
    /// <param name="offset">The producer's offset of <paramref name="text"/>; makes the append idempotent.</param>
    /// <returns>The document's state after the append.</returns>
    public static IObservable<AppendDocumentTextResponse> AppendToDocument(
        this IMessageHub hub, string documentPath, string text, long? offset = null) =>
        hub.AppendToDocument(documentPath, new AppendDocumentTextRequest { Text = text ?? string.Empty, Offset = offset });

    /// <summary>Appends <paramref name="finalText"/> and seals the document: the trailing part is written. Cold.</summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="documentPath">The logical document's path.</param>
    /// <param name="finalText">Optional last text.</param>
    /// <param name="offset">The producer's offset of <paramref name="finalText"/>.</param>
    /// <returns>The sealed document's state.</returns>
    public static IObservable<AppendDocumentTextResponse> CompleteDocument(
        this IMessageHub hub, string documentPath, string finalText = "", long? offset = null) =>
        hub.AppendToDocument(documentPath,
            new AppendDocumentTextRequest { Text = finalText ?? string.Empty, Offset = offset, Complete = true });

    /// <summary>Posts <paramref name="request"/> to the document's hub. Cold.</summary>
    /// <param name="hub">The calling hub.</param>
    /// <param name="documentPath">The logical document's path.</param>
    /// <param name="request">The append.</param>
    /// <returns>The document's state after the append.</returns>
    public static IObservable<AppendDocumentTextResponse> AppendToDocument(
        this IMessageHub hub, string documentPath, AppendDocumentTextRequest request)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentException.ThrowIfNullOrEmpty(documentPath);
        ArgumentNullException.ThrowIfNull(request);
        return Observable.Defer(() => hub.NodeOperationIssuingHub()
                .Observe(request, o => o.WithTarget(new Address(documentPath))))
            .Take(1)
            .Select(delivery => delivery.Message);
    }

    /// <summary>True when this host has the module that writes document logs.</summary>
    /// <param name="hub">Any hub of the host.</param>
    /// <returns>Whether <see cref="DocumentLogSupport"/> is registered.</returns>
    public static bool SupportsDocumentLogs(this IMessageHub hub) =>
        hub.ServiceProvider.GetService<DocumentLogSupport>() is not null;

    private static bool IsAlreadyExists(Exception ex) =>
        ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
}
