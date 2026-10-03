using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// WHERE a consumer reads a stream's items from: an ordered set of child nodes — a namespace, how a
/// child's id maps to its sequence, and how a child becomes a payload. The default
/// (<see cref="ItemsOf"/>) is the stream's own <c>_DurableStreamItem</c> nodes. Any other
/// durable, ordered, idempotent-by-index child set can be consumed as a stream WITHOUT a second
/// copy — a document's <c>_DocumentPart/{index:D6}</c> children are one (sequence = index + 1):
/// <code>
/// new DurableStreamSource&lt;DocumentPart&gt;(
///     DocumentPartPaths.PartNamespace(documentPath),
///     node =&gt; DocumentPartPaths.TryParsePartIndex(node.Id, out var i) ? i + 1 : null,
///     (node, options) =&gt; node.ContentAs&lt;DocumentPart&gt;(options)!)
/// </code>
/// </summary>
/// <typeparam name="T">The payload type.</typeparam>
/// <param name="ItemsNamespace">The namespace whose children are the items.</param>
/// <param name="SequenceOf">A child's sequence (1-based, contiguous), or null when the child is not an item.</param>
/// <param name="PayloadOf">A child's payload, read with the consuming hub's serializer options.</param>
public sealed record DurableStreamSource<T>(
    string ItemsNamespace,
    Func<MeshNode, long?> SequenceOf,
    Func<MeshNode, JsonSerializerOptions, T> PayloadOf)
{
    /// <summary>The stream's own item nodes, payload deserialized as <typeparamref name="T"/>.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The source.</returns>
    public static DurableStreamSource<T> ItemsOf(DurableStreamId stream) => new(
        DurableStreamPaths.ItemsNamespace(stream.Validated()),
        node => DurableStreamPaths.SequenceOf(node.Id),
        (node, options) =>
        {
            var item = node.ContentAs<DurableStreamItemContent>(options)
                ?? throw new JsonException($"{node.Path} carries no {nameof(DurableStreamItemContent)}");
            return JsonSerializer.Deserialize<T>(item.PayloadJson, options)
                ?? throw new JsonException($"{node.Path}: the payload deserialized to null");
        });
}

/// <summary>
/// Producing to and reading from a durable stream, from a hub.
/// </summary>
public static class DurableStreamHubExtensions
{
    /// <summary>
    /// Durably appends <paramref name="payload"/> to <paramref name="stream"/>: the stream node's
    /// hub assigns the next sequence and creates the item node, and only then does this emit the
    /// sequence (once) and complete. Cold — nothing is written until subscribed. The stream node is
    /// created on first use. A failed append surfaces as <c>OnError</c>, never as silence.
    /// </summary>
    /// <typeparam name="T">The payload type (serialized as its runtime type with this hub's options).</typeparam>
    /// <param name="hub">The publishing hub.</param>
    /// <param name="stream">The target stream.</param>
    /// <param name="payload">The payload; must not be null.</param>
    /// <returns>The assigned sequence.</returns>
    public static IObservable<long> PublishDurable<T>(this IMessageHub hub, DurableStreamId stream, T payload)
        => Observable.Defer(() =>
        {
            stream.Validated();
            if (payload is null)
                throw new ArgumentNullException(nameof(payload), $"A durable stream item for {stream} needs a payload.");
            var json = JsonSerializer.Serialize(payload, payload.GetType(), hub.JsonSerializerOptions);
            return hub.ToStreamHub<AppendDurableStreamItemResponse>(stream, new AppendDurableStreamItemRequest(json))
                .Select(response => response.Error is null
                    ? response.Sequence
                    : throw new InvalidOperationException($"Appending to durable stream {stream} failed: {response.Error}"));
        });

    /// <summary>
    /// Reads <paramref name="source"/> strictly after <paramref name="afterSequence"/>: every item
    /// exactly once, in sequence order, then every item appended later, live. Never completes on its
    /// own. Unleased — a plain reader; consuming with an acknowledged checkpoint is
    /// <see cref="DurableStreamConsumerExtensions.WithDurableStreamConsumer{T}(MessageHubConfiguration, string, Func{IMessageHub, string}, Func{IMessageHub, DurableStreamItem{T}, IObservable{System.Reactive.Unit}})"/>.
    ///
    /// <para>One synced query over the items' namespace provides both halves: its initial result is
    /// the backlog, its later <c>Added</c> changes are the live tail (the mesh change feed — Postgres
    /// <c>LISTEN</c> across pods). Items are released strictly in sequence: one that arrives ahead of
    /// a missing predecessor waits for it, one at or below the cursor is a duplicate and is dropped.</para>
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="hub">The reading hub (its serializer options read the payloads).</param>
    /// <param name="stream">The stream (names the items in the emitted <see cref="DurableStreamItem{T}"/>).</param>
    /// <param name="source">Where the items are.</param>
    /// <param name="afterSequence">The last sequence already processed (0 = from the start).</param>
    /// <returns>The items.</returns>
    public static IObservable<DurableStreamItem<T>> ReadDurable<T>(
        this IMessageHub hub, DurableStreamId stream, DurableStreamSource<T> source, long afterSequence)
        => Observable.Defer(() =>
        {
            var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var access = hub.ServiceProvider.GetService<AccessService>();
            // Cursor and reorder buffer: written only inside the synchronized chain below.
            var next = afterSequence + 1;
            var waiting = ImmutableSortedDictionary<long, MeshNode>.Empty;

            return access.RunAsSystem(() => meshService.Query<MeshNode>(
                        MeshQueryRequest.FromQuery($"namespace:{source.ItemsNamespace}").Complete()))
                .Where(change => change.ChangeType is not QueryChangeType.Removed)
                .SelectMany(change => change.Items)
                .Synchronize()
                .SelectMany(node =>
                {
                    if (source.SequenceOf(node) is not { } sequence || sequence < next)
                        return ImmutableList<MeshNode>.Empty;
                    waiting = waiting.SetItem(sequence, node);
                    var ready = ImmutableList.CreateBuilder<MeshNode>();
                    while (waiting.TryGetValue(next, out var due))
                    {
                        ready.Add(due);
                        waiting = waiting.Remove(next);
                        next++;
                    }
                    return ready.ToImmutable();
                })
                .Select(node => new DurableStreamItem<T>(stream, source.SequenceOf(node)!.Value, ReadPayload(hub, source, node)));
        });

    /// <summary>
    /// <see cref="ReadDurable{T}(IMessageHub, DurableStreamId, DurableStreamSource{T}, long)"/> over the
    /// stream's own item nodes.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="hub">The reading hub.</param>
    /// <param name="stream">The stream.</param>
    /// <param name="afterSequence">The last sequence already processed.</param>
    /// <returns>The items.</returns>
    public static IObservable<DurableStreamItem<T>> ReadDurable<T>(this IMessageHub hub, DurableStreamId stream, long afterSequence)
        => hub.ReadDurable(stream, DurableStreamSource<T>.ItemsOf(stream), afterSequence);

    private static T ReadPayload<T>(IMessageHub hub, DurableStreamSource<T> source, MeshNode node)
    {
        try
        {
            return source.PayloadOf(node, hub.JsonSerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidCastException)
        {
            // Never a silent skip: the item is named and the reader stops AT it.
            throw new DurableStreamPayloadException(node.Path, typeof(T), ex);
        }
    }

    /// <summary>
    /// A request to the stream node's hub, issued from the mesh's read-issuing hub.
    ///
    /// <para>🚨 The stream node is ENSURED before the first request this process sends to it —
    /// never discovered by routing to it. A request routed to a path that does not exist yet is
    /// answered NotFound, and on the Orleans route that refusal outlives the node's creation: a
    /// resend right after a successful create was refused the same way (measured, Orleans
    /// TestCluster). So the order is create-then-send, once per stream per process
    /// (<see cref="DurableStreamDirectory"/>); a refusal after that (the node was deleted) re-ensures
    /// and resends once.</para>
    ///
    /// <para>Only a PRODUCER creates a stream (<paramref name="createIfMissing"/>). A consumer never
    /// does: it only sends to a stream it has seen exist, or one whose append woke it.</para>
    /// </summary>
    internal static IObservable<TResponse> ToStreamHub<TResponse>(
        this IMessageHub hub, DurableStreamId stream, IRequest<TResponse> request, bool createIfMissing = true)
    {
        var target = new Address(DurableStreamPaths.StreamPath(stream));
        // 🚨 Issued from the MESH's read-issuing hub, never from the caller: a consumer that is
        // quiescing (a recycle waiting for its item in flight) may no longer post requests of its
        // own, and its acknowledgement and release are exactly what must still go out then. The
        // subscriber is named in the request instead (see ClaimDurableStreamRequest).
        var issuer = hub.GetMeshHub().ReadIssuingHub();
        var directory = hub.ServiceProvider.GetRequiredService<DurableStreamDirectory>();
        IObservable<TResponse> Send() => issuer.Observe(request, o => o.WithTarget(target)).Take(1).Select(d => d.Message);
        IObservable<System.Reactive.Unit> Ensure() => EnsureStreamNode(hub, stream).Do(_ => directory.MarkEnsured(stream));

        if (!createIfMissing)
            return Send();
        return (directory.IsEnsured(stream) ? Send() : Ensure().SelectMany(_ => Send()))
            .Catch<TResponse, DeliveryFailureException>(refused =>
            {
                hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(DurableStreamHub))
                    .LogWarning(refused, "[DurableStreams] {Stream}: the stream node refused {Request}; ensuring it exists and resending once",
                        stream, request.GetType().Name);
                directory.Forget(stream);
                return Ensure().SelectMany(_ => Send());
            });
    }

    /// <summary>
    /// Whether the stream node exists — ONE query (a listing read, never a point read of a node that
    /// may be absent, which would answer NotFound and arm the read path's negative cache). Its
    /// negative may be a moment stale; that is harmless here, because a producer's first append to
    /// a stream nobody holds WAKES the owner.
    /// </summary>
    internal static IObservable<bool> StreamExists(this IMessageHub hub, DurableStreamId stream)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => meshService.Query<MeshNode>(
                    MeshQueryRequest.FromQuery($"path:{DurableStreamPaths.StreamPath(stream)}")))
            .Where(change => change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1)
            .Select(change => change.Items.Any());
    }

    private static IObservable<System.Reactive.Unit> EnsureStreamNode(IMessageHub hub, DurableStreamId stream)
    {
        var node = new MeshNode(stream.Namespace, $"{stream.Key.TrimEnd('/')}/{DurableStreamPaths.StreamSegment}")
        {
            NodeType = DurableStreamNodeTypes.Stream,
            Name = $"Durable stream · {stream.Namespace}",
            MainNode = stream.Key,
            State = MeshNodeState.Active,
            Content = new DurableStreamState { Namespace = stream.Namespace, Key = stream.Key },
        };
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => meshService.CreateNode(node))
            .Take(1)
            .Select(_ => System.Reactive.Unit.Default)
            // "Already exists" — another producer, an earlier process, or the race between two first
            // sends — is the expected answer most of the time: the node is there, which is all we
            // need. Any other refusal is real and the request that follows surfaces it.
            .Catch<System.Reactive.Unit, Exception>(_ => Observable.Return(System.Reactive.Unit.Default));
    }
}

/// <summary>
/// An item of a durable stream could not be read as the type its consumer asked for. Names the item
/// node and the requested type; the consumer stops AT that item rather than skipping it.
/// </summary>
public sealed class DurableStreamPayloadException(string itemPath, Type requested, Exception inner)
    : Exception($"Durable stream item {itemPath} could not be read as {requested.Name}: {inner.Message}", inner)
{
    /// <summary>The unreadable item node.</summary>
    public string ItemPath { get; } = itemPath;
}

/// <summary>
/// Which stream nodes this process has already ensured — so the create that must precede a stream's
/// first request runs once per stream per process, not once per append. Instance state of a mesh
/// singleton (never static): it dies with the mesh, and forgetting a stream only costs one more
/// idempotent create.
/// </summary>
public sealed class DurableStreamDirectory
{
    private ImmutableHashSet<string> ensured = ImmutableHashSet<string>.Empty;

    /// <summary>Whether the stream's node was ensured by this process.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>True when ensured.</returns>
    public bool IsEnsured(DurableStreamId stream) => Volatile.Read(ref ensured).Contains(DurableStreamPaths.StreamPath(stream));

    /// <summary>Records that the stream's node exists.</summary>
    /// <param name="stream">The stream.</param>
    public void MarkEnsured(DurableStreamId stream) =>
        ImmutableInterlocked.Update(ref ensured, set => set.Add(DurableStreamPaths.StreamPath(stream)));

    /// <summary>Forgets the stream (its node refused a request), so the next use ensures it again.</summary>
    /// <param name="stream">The stream.</param>
    public void Forget(DurableStreamId stream) =>
        ImmutableInterlocked.Update(ref ensured, set => set.Remove(DurableStreamPaths.StreamPath(stream)));
}
