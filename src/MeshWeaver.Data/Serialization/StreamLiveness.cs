using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// 🚨 THE ONE liveness predicate for a CACHED synchronization stream — used by every
/// reference-keyed stream cache in the codebase, deliberately in exactly one place.
///
/// <para><b>Why one place.</b> Three caches ask the same question — <c>Workspace._localStreamCache</c>,
/// <c>Workspace._remoteStreamCache</c> and <c>SynchronizationStream.sharedReduceCache</c> — and
/// they had three hand-copied answers. When <c>ReduceShared</c> was added (#1425) its copy was
/// found to be insufficient and grew a parent guard; the two in <see cref="Workspace"/> did not,
/// and one of them was character-for-character the predicate that had just been ruled inadequate
/// (Systemorph/MeshWeaver#1455). A predicate that is copied is a predicate that diverges, so this
/// is the only definition and <c>StreamCacheLivenessTest</c> pins that every cache serves the same
/// verdict.</para>
///
/// <para>🚨 <b>A cached child is NOT a descendant of its parent, so "is the child alive?" is never
/// sufficient.</b> <c>WorkspaceStreams.CreateReducedStream</c> builds the reduced stream's
/// <c>sync/{id}</c> sub-hub under <c>stream.Host</c> — making it the parent's SIBLING — and merely
/// registers it for disposal ON the parent. So when the parent dies the child stays alive for the
/// whole teardown cascade, and a check that looks only at the child happily hands back a mirror of
/// a dead source. Both observed failures of #1455 come from that one gap:</para>
/// <list type="bullet">
/// <item><description><b>During the cascade</b> — the cache serves the very same child. Its
/// replay subject still hands out the last value it ever saw, and then nothing: no further
/// emission (the source is gone) and no completion (the child is not disposed yet). A reader
/// binds to it and hangs for its whole budget on a stale snapshot; a writer patches a stream
/// nobody drains and is acked.</description></item>
/// <item><description><b>After the cascade</b> — the child is disposed, so the cache evicts and
/// re-reduces; but a reduce off a disposed parent is disposed on birth
/// (<c>SynchronizationStream.RegisterForDisposal</c> disposes immediately once the parent is
/// terminal), so the replacement fails the same check and the loop never terminates.</description></item>
/// </list>
///
/// <para>🚨 <b>A FAULTED stream is dead in exactly the same way, and that had to be said out loud
/// (Systemorph/MeshWeaver#2387).</b> A store is a <c>ReplaySubject</c>, so a terminal <c>OnError</c>
/// is permanent under the Rx grammar — the stream can never emit again, and every later subscriber
/// replays that error INSTANTLY. Nothing disposes it either: the terminal arm of
/// <c>CreateExternalClient</c> faults the stream and tears down only its keep-alive, leaving the
/// object "errored but undisposed". So while this predicate asked about disposal alone, a cache
/// kept handing the corpse back for the whole process lifetime, and the symptom was the opposite of
/// a hang: a 30-second initial-state bound reported after 0.07 ms, because nothing waited for
/// anything. Refusing to serve it is not a retry — the next natural caller opens a fresh stream,
/// and a stream that is BORN dead is handed back rather than re-created, so create → fault → create
/// cannot spin.</para>
///
/// <para><b>The verdict.</b> A stream is usable when it and every ancestor in its reduce chain is
/// undisposed, unfaulted, and owns a hub that has not begun winding down.
/// <see cref="MessageHub.IsDisposing"/> is checked as well as <see cref="IMessageHub.RunLevel"/>
/// because hub shutdown is asynchronous: right after <c>Dispose()</c> the RunLevel still reads
/// <c>Started</c> while hosted-hub creation is already frozen.</para>
/// </summary>
internal static class StreamLiveness
{
    /// <summary>
    /// The chain is a reduce chain (primary → intermediate → leaf), so it is short by construction —
    /// two or three links in every production shape. The bound exists only so a future cycle cannot
    /// turn a liveness probe into a hang.
    /// </summary>
    private const int MaxChainDepth = 64;

    /// <summary>
    /// True when <paramref name="stream"/> may be handed to a caller from a cache: it and every
    /// ancestor in its reduce chain are undisposed, unfaulted and hub-backed.
    /// </summary>
    /// <param name="stream">The stream to judge, or <c>null</c>.</param>
    /// <returns><c>false</c> for <c>null</c>, for a disposed or terminally faulted stream, and for
    /// a live stream whose source chain contains one.</returns>
    public static bool IsUsable(ISynchronizationStream? stream)
    {
        if (stream is null)
            return false;

        var current = stream;
        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            // 🚨 FAULTED counts exactly as much as DISPOSED — Systemorph/MeshWeaver#2387.
            // A stream's store is a ReplaySubject, so a terminal OnError is permanent: it can
            // never emit again and every later subscriber replays that same error INSTANTLY. A
            // cache that judged liveness on disposal alone therefore kept handing the corpse back
            // for the whole process lifetime, and the symptom was the opposite of a hang — a
            // 30-second bound reported after 0.07 ms, because nothing waited for anything. On the
            // boot path that turned ONE unanswered SubscribeRequest to a busy per-node hub into a
            // package this pod could never install again, including by the installer's own
            // fall-back-to-full-install repair, which re-entered the same dead mirror.
            if (current is IStreamLivenessSource { IsDisposed: true } or IStreamLivenessSource { IsFaulted: true })
                return false;
            if (current.Hub is not { } hub
                || hub.RunLevel > MessageHubRunLevel.Started
                || hub is MessageHub { IsDisposing: true })
                return false;

            // Walked off the end of the chain with every link alive — the only way to say yes.
            if ((current as IStreamLivenessSource)?.Source is not { } parent)
                return true;
            current = parent;
        }

        // 🚨 The cap is a guard against a cycle that should not exist, so reaching it means the
        // chain is not something this predicate understands. FAIL CLOSED: an unverifiable stream is
        // not served from cache. The caller falls through to a fresh reduce, which is correct
        // whether the chain was merely long or genuinely circular.
        return false;
    }

    /// <summary>
    /// True when <paramref name="stream"/> ITSELF took a terminal error — the one reason for being
    /// unusable that a cache can and must CLOSE, rather than merely stop serving.
    ///
    /// <para>The distinction matters at teardown. A stream that is unusable because its hub is
    /// winding down is already being disposed by that cascade, and closing it a second time from a
    /// cache lookup would post an <c>UnsubscribeRequest</c> at a dying owner — which on Orleans
    /// re-activates the very grain the teardown is retiring. A FAULTED stream has no such owner:
    /// its store is terminal, so it can neither notify a reader nor be revived, and nothing else
    /// will ever close it. Deliberately does NOT walk the source chain — a faulted ancestor is the
    /// ancestor's cache's business, not this one's.</para>
    /// </summary>
    /// <param name="stream">The stream to judge, or <c>null</c>.</param>
    /// <returns><c>true</c> only when this exact stream's store holds a terminal error.</returns>
    public static bool HasFaulted(ISynchronizationStream? stream)
        => stream is IStreamLivenessSource { IsFaulted: true };
}

/// <summary>
/// The liveness facts a reduced stream can answer about ITSELF and its source — deliberately a
/// separate INTERNAL interface rather than members on the public
/// <see cref="ISynchronizationStream"/>.
///
/// <para>Two reasons. Adding a member to a public interface breaks any downstream implementer, and
/// MeshWeaver ships these assemblies as packages. And <see cref="Source"/> is not a consumer-facing
/// concept: it exists so <see cref="StreamLiveness.IsUsable"/> can walk the reduce chain in one
/// place, and a consumer walking it by hand is the ad-hoc predicate this whole change exists to
/// remove.</para>
/// </summary>
internal interface IStreamLivenessSource
{
    /// <summary>
    /// The stream this one was reduced FROM, or <c>null</c> for a stream that is not a reduce of
    /// another (a data source's primary stream, a combined stream, a remote mirror).
    ///
    /// <para>🚨 A reduced stream is its parent's SIBLING, not its child:
    /// <c>WorkspaceStreams.CreateReducedStream</c> hosts its <c>sync/{id}</c> sub-hub under the
    /// parent's <c>Host</c> and only registers it for disposal on the parent. Without this link,
    /// "is this stream alive?" cannot see that the thing it mirrors is already dead — the gap behind
    /// Systemorph/MeshWeaver#1455.</para>
    /// </summary>
    ISynchronizationStream? Source { get; }

    /// <summary>
    /// Whether this stream has been disposed. Its store is completed and it will never emit again.
    /// </summary>
    bool IsDisposed { get; }

    /// <summary>
    /// Whether this stream's store took a terminal error. It will never emit again either, and —
    /// unlike a completed store — every later subscriber replays that error the instant it
    /// subscribes, so serving it from a cache turns one transient failure into a permanent one.
    /// </summary>
    bool IsFaulted { get; }
}
