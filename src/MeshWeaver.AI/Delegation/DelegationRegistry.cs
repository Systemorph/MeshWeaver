using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MeshWeaver.AI.Delegation;

/// <summary>
/// Per-<c>_Exec</c>-hub registry of in-flight delegations. Owned by the
/// hub's DI scope (single instance per hub). Access is restricted to the
/// hub's handlers — they run serialized on the action block, so the
/// dictionary access is logically single-threaded even though
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> is used as the storage
/// primitive (for the cheap thread-safe TryAdd/TryRemove semantics during
/// startup/shutdown windows).
///
/// <para>Each entry owns:</para>
/// <list type="bullet">
///   <item>The <see cref="ChannelWriter{T}"/> that
///         <c>ExecuteDelegationAsync</c>'s <c>await foreach</c> drains.</item>
///   <item>The <c>SubThreadPath</c> the entry observes.</item>
///   <item>The accumulated text seen so far — read in the
///         <c>SubThreadStateChanged</c> handler to compute the next text
///         delta to emit onto the channel.</item>
///   <item>The single observation subscription, disposed on terminal.</item>
/// </list>
///
/// <para>Created in <c>ExecuteDelegationAsync</c> and removed when the
/// terminal frame is written. A dropped FCC turn cleans the entry via
/// the <c>cancellationToken.Register</c> hook installed in
/// <c>ExecuteDelegationAsync</c>.</para>
/// </summary>
internal sealed class DelegationRegistry
{
    public ConcurrentDictionary<string, DelegationEntry> Active { get; } = new();
}

/// <summary>
/// A single delegation's in-flight state. <see cref="AccumulatedText"/>
/// is mutated by the <c>SubThreadStateChanged</c> handler (single-threaded
/// on the <c>_Exec</c> action block), which is why a plain string suffices
/// rather than a thread-safe accumulator.
/// </summary>
internal sealed class DelegationEntry
{
    public required string CallId { get; init; }
    public required string SubThreadPath { get; init; }
    public required string ResponseMsgId { get; init; }
    public required ChannelWriter<DelegationFrame> Writer { get; init; }
    public string AccumulatedText { get; set; } = "";
    public IDisposable? Subscription { get; set; }
}

/// <summary>
/// A frame written to the per-delegation channel and yielded by
/// <c>ExecuteDelegationAsync</c>'s <c>await foreach</c>. Either a text
/// delta (yielded to the parent agent's tool-call result accumulator) or
/// a terminal marker that closes the loop with a final status.
/// </summary>
internal sealed record DelegationFrame(
    string? Delta,
    bool Terminal,
    ThreadMessageStatus? FinalStatus,
    string? ErrorMessage);
