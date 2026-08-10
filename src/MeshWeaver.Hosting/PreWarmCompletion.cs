using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Hosting;

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
/// <para>The one-shot replay is an <see cref="AsyncSubject{T}"/>, the same idiom
/// <c>InstanceAutoRegistrationService.Completed</c> uses: whoever subscribes after the bake
/// settled proceeds immediately; whoever subscribes before waits without polling. Deliberately
/// NOT settled on host shutdown — a consumer's own subscription teardown is what cancels its
/// wait, and settling mid-shutdown would START the very work being torn down.</para>
/// </summary>
public sealed class PreWarmCompletion : IDisposable
{
    private readonly AsyncSubject<Unit> completed = new();

    /// <summary>
    /// Emits exactly once, when the bake has settled, and replays that emission to late
    /// subscribers. Never emits on a host whose pre-warm hosted service was registered but not
    /// started (the subscription is expected to be torn down with its owner).
    /// </summary>
    public IObservable<Unit> Completed => completed.AsObservable();

    /// <summary>
    /// The bake settled: the sweep completed, faulted (lazy compile still applies), or never
    /// applied (pre-warm disabled, no mesh hub). Idempotent — a second call is a no-op.
    /// </summary>
    public void MarkSettled()
    {
        completed.OnNext(Unit.Default);
        completed.OnCompleted();
    }

    /// <inheritdoc />
    public void Dispose() => completed.Dispose();
}
