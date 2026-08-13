using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Hosting;

/// <summary>
/// How this host's boot bake SETTLED. The three cases are deliberately distinct because they are
/// not equally informative, and a consumer that cannot tell them apart cannot log honestly about
/// what it sequenced behind.
/// </summary>
public enum PreWarmSettlement
{
    /// <summary>
    /// There was no bake to run: the pre-warm is disabled (<c>PreWarm:DynamicTypes</c> is not true)
    /// or no mesh hub resolved. Nothing was attempted, so nothing is known — and nothing is claimed.
    /// </summary>
    NotApplicable,

    /// <summary>
    /// The sweep RAN TO COMPLETION. Every type it enumerated reached a terminal outcome, and the
    /// compile queue has drained. This says the sweep finished — NOT that every type is healthy:
    /// a completed sweep can still have recorded regressions, which live on
    /// <see cref="NodeTypeBakeGateState"/>, the surface that actually decides readiness.
    /// </summary>
    Completed,

    /// <summary>
    /// The sweep ERRORED — typically the enumeration query threw or timed out — so it verified
    /// nothing. Lazy compilation still covers correctness, so a consumer sequenced behind the bake
    /// may proceed; it just must not report that a bake happened.
    /// </summary>
    Faulted,
}

/// <summary>
/// The boot bake's completion signal: emits once when this host's dynamic-NodeType pre-warm has
/// SETTLED — ran to completion, faulted, or was disabled — and replays that emission to late
/// subscribers. Registered by
/// <see cref="PreWarmServiceCollectionExtensions.AddDynamicTypePreWarming"/>; a host that never
/// registers the pre-warm has no instance, and consumers treat "absent" as "nothing to wait for".
///
/// <para>🚨 Why this exists (#1114): boot work that WRITES THROUGH per-node hubs must not race the
/// bake. On a pre-warming host every framework roll leaves ~240 dynamic NodeTypes ABI-stale, and
/// the sweep rebuilds them sequentially (~10 min measured across three production portals).
/// While that queue drains, ANY touch of a node whose NodeType is dynamic parks its per-node hub
/// activation on the type's rebuild (<c>NodeTypeEnrichmentHelpers</c>' framework-stale heal flips
/// the stale-Ok stamp to Pending and waits — correctly — for the compile). A caller with its own
/// bound, like the installer's cross-hub <c>stream.Update</c> and its 30 s initial-state wait,
/// then aborts on every single package — which is exactly how every pod boot on the memex portal
/// installed ZERO default plugins for months of restarts. The fix is ordering, not a bigger
/// timeout: boot flows that activate per-node hubs sequence themselves after this signal.</para>
///
/// <para>🚨 The emission CARRIES ITS OUTCOME (<see cref="PreWarmSettlement"/>). It used to be
/// <c>IObservable&lt;Unit&gt;</c> and fired identically whether the bake completed, faulted, or was
/// never enabled — so a consumer waiting on it could not say what it had waited for, and a boot
/// where the sweep errored was indistinguishable in every downstream log from a boot where ~240
/// types were verified. Ordering is the same in all three cases (see
/// <c>InstanceAutoRegistrationService</c> for why proceeding is right even on a fault), but
/// "proceed regardless" is a conclusion a consumer should reach from the value, not a distinction
/// the signal quietly withholds from it.</para>
///
/// <para>The one-shot replay is an <see cref="AsyncSubject{T}"/>, the same idiom
/// <c>InstanceAutoRegistrationService.Completed</c> uses: whoever subscribes after the bake
/// settled proceeds immediately; whoever subscribes before waits without polling. Deliberately
/// NOT settled on host shutdown — a consumer's own subscription teardown is what cancels its
/// wait, and settling mid-shutdown would START the very work being torn down.</para>
/// </summary>
public sealed class PreWarmCompletion : IDisposable
{
    private readonly AsyncSubject<PreWarmSettlement> settled = new();

    /// <summary>
    /// Emits exactly once, when the bake has settled, carrying HOW it settled, and replays that
    /// emission to late subscribers. Never emits on a host whose pre-warm hosted service was
    /// registered but not started (the subscription is expected to be torn down with its owner).
    /// </summary>
    public IObservable<PreWarmSettlement> Settled => settled.AsObservable();

    /// <summary>
    /// The bake settled: the sweep completed, faulted (lazy compile still applies), or never
    /// applied (pre-warm disabled, no mesh hub). Idempotent — a second call is a no-op, and the
    /// FIRST outcome wins, because the earliest terminal is the one that actually happened.
    /// </summary>
    public void MarkSettled(PreWarmSettlement settlement)
    {
        settled.OnNext(settlement);
        settled.OnCompleted();
    }

    /// <inheritdoc />
    public void Dispose() => settled.Dispose();
}
