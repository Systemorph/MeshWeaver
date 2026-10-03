using System.Globalization;

namespace MeshWeaver.Mesh;

/// <summary>
/// Names ONE durable stream. A durable stream IS a set of saved mesh nodes
/// (Doc/Architecture/DurableStreams): a stream node that orders and leases it, and one item node
/// per appended item. Nothing about a stream lives anywhere but in the mesh.
///
/// <para><see cref="Key"/> is the mesh path of the node that OWNS the stream — the document a log
/// belongs to, the job a work stream belongs to; for an instance-wide stream a path under
/// <c>Admin</c>. The stream's nodes are satellites of that owner, so they live in the owner's
/// partition and are readable exactly by those who may read the owner. <see cref="Namespace"/> is
/// the stream FAMILY (e.g. <c>DocumentLog</c>): one owner can carry one stream per family, and a
/// hub subscribes to a family by configuration.</para>
/// </summary>
/// <param name="Namespace">The stream family.</param>
/// <param name="Key">The mesh path of the owning node.</param>
public sealed record DurableStreamId(string Namespace, string Key)
{
    /// <summary>Fails loudly on an unusable id.</summary>
    /// <returns>This id, for fluent use.</returns>
    /// <exception cref="ArgumentException">When <see cref="Namespace"/> or <see cref="Key"/> is
    /// empty, or the namespace contains a <c>/</c>.</exception>
    public DurableStreamId Validated()
    {
        if (string.IsNullOrWhiteSpace(Namespace) || Namespace.Contains('/'))
            throw new ArgumentException($"A durable stream needs a non-empty Namespace without '/' (was '{Namespace}').", nameof(Namespace));
        if (string.IsNullOrWhiteSpace(Key))
            throw new ArgumentException($"Durable stream '{Namespace}' needs the owning node's path as Key.", nameof(Key));
        return this;
    }

    /// <summary><c>{Namespace}@{Key}</c> — for logs only, never parsed back.</summary>
    public override string ToString() => $"{Namespace}@{Key}";
}

/// <summary>
/// One item of a durable stream as a consumer sees it: the stream, the item's
/// <see cref="Sequence"/> — assigned by the stream's own hub when the item node was created,
/// 1-based, contiguous and strictly increasing per stream — and the typed payload. The sequence is
/// the rewind token: a consumer resumes strictly after the last sequence it acknowledged.
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
/// <param name="Stream">The stream the item belongs to.</param>
/// <param name="Sequence">The item's per-stream sequence (1, 2, 3, …).</param>
/// <param name="Payload">The payload, read for the consumer.</param>
public sealed record DurableStreamItem<T>(DurableStreamId Stream, long Sequence, T Payload);

/// <summary>
/// Where a durable stream's nodes live. All paths are pure functions of the
/// <see cref="DurableStreamId"/>:
/// <code>
/// {Key}/_DurableStream/{Namespace}                                   DurableStream      (owner's mesh_nodes)
/// {Key}/_DurableStream/{Namespace}/_DurableStreamItem/000000000001   DurableStreamItem  (owner's durable_stream_items)
/// </code>
/// </summary>
public static class DurableStreamPaths
{
    /// <summary>The satellite segment the stream node sits under.</summary>
    public const string StreamSegment = "_DurableStream";

    /// <summary>
    /// The satellite segment of the items — mapped to their own table
    /// (<see cref="ItemTable"/>). 🚨 Deliberately LONGER than every satellite a stream owner may sit
    /// under (<c>_Activity</c> 9, <c>_DocumentPart</c> 13, <c>_ThreadMessage</c> 14): a storage
    /// adapter places a node by the LONGEST mapped segment anywhere in its path.
    /// </summary>
    public const string ItemSegment = "_DurableStreamItem";

    /// <summary>The partition table the items are stored in.</summary>
    public const string ItemTable = "durable_stream_items";

    /// <summary>The stream node's path.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns><c>{Key}/_DurableStream/{Namespace}</c>.</returns>
    public static string StreamPath(DurableStreamId stream) =>
        $"{stream.Key.TrimEnd('/')}/{StreamSegment}/{stream.Namespace}";

    /// <summary>The namespace the stream's item nodes are created in.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns><c>{StreamPath}/_DurableStreamItem</c>.</returns>
    public static string ItemsNamespace(DurableStreamId stream) => $"{StreamPath(stream)}/{ItemSegment}";

    /// <summary>An item's node id: the sequence, zero-padded to 12 digits so ids sort as numbers.</summary>
    /// <param name="sequence">The item's sequence.</param>
    /// <returns>The node id.</returns>
    public static string ItemId(long sequence) => sequence.ToString("D12", CultureInfo.InvariantCulture);

    /// <summary>Reads an item's sequence back from its node id.</summary>
    /// <param name="id">The node id.</param>
    /// <returns>The sequence, or null when <paramref name="id"/> is not an item id.</returns>
    public static long? SequenceOf(string? id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) && sequence > 0
            ? sequence
            : null;
}
