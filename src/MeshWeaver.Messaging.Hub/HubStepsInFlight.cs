using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Messaging;

/// <summary>
/// 🚨 The ONE in-flight marker of a hub: whether a STEP of work this hub's activation owns is
/// running right now, and the signal that it has ended (#7324). Every hub carries exactly one,
/// created with the hub and living in its property bag, so it is the hub's own instance state. It
/// is never static and never shared between hubs.
///
/// <para><b>Host-independent.</b> A step enters it through
/// <see cref="MessageHubExtensions.BeginAsyncOperation"/>. That is the same call on every host. On
/// Orleans it also pins the grain against idle collection; in-process there is no grain to pin, and
/// the marker is the whole of it. Before #7324 the in-process form was a no-op, so nothing anywhere
/// could tell "this hub is mid-step" from "this hub is idle".</para>
///
/// <para><b>Who reads it.</b> A recycle that must not drop a step. A live module swap's recycle wave
/// (<c>ModuleLiveUpdater</c>) DEFERS a hub that has a step in flight: it subscribes to
/// <see cref="Idle"/> and posts the hub's <see cref="DisposeRequest"/> when the step ends. Before
/// this, the wave tore a module reload request's hub down in the middle of its Landing step, the
/// next activation started the landing over, and the request re-entered Landing again and again.</para>
///
/// <para><b>No gate.</b> Nothing waits on it inside the hub, and it blocks no thread. It is a
/// counter plus a hot "ended" signal. The release runs exactly once, from the scope's
/// <c>Dispose</c>. Every step holds that scope in an <c>Observable.Using</c> or a
/// <c>using</c>, which is the step's finally, so a faulted or unsubscribed step releases its
/// mark like a completed one.</para>
/// </summary>
public sealed class HubStepsInFlight
{
    private readonly ISubject<Unit> ended = Subject.Synchronize(new Subject<Unit>());
    private int count;

    /// <summary>How many steps are in flight on this hub right now.</summary>
    public int Count => Volatile.Read(ref count);

    /// <summary>
    /// Marks one step in flight until the returned scope is disposed. Disposing more than once
    /// releases once. Dispose it in the step's finally: <c>Observable.Using</c> or <c>using</c>.
    /// </summary>
    /// <returns>The step's mark.</returns>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref count);
        return Disposable.Create(End);
    }

    private void End()
    {
        if (Interlocked.Decrement(ref count) == 0)
            ended.OnNext(Unit.Default);
    }

    /// <summary>
    /// Emits once, then completes, at the first instant no step is in flight. It emits at once when
    /// none is in flight on subscribe. Hot underneath and never replays: the "ended" signal is
    /// subscribed BEFORE the count is read, so a step that ends between the two is not missed. It
    /// does not complete on its own when the hub dies, so compose it with the hub's teardown
    /// (<see cref="IMessageHub.RunLevelChanged"/>), as the swap wave does.
    /// </summary>
    /// <returns>One emission when the hub is idle.</returns>
    public IObservable<Unit> Idle() =>
        Observable.Defer(() => ended.Merge(Observable.Return(Unit.Default)))
            .Where(_ => Count == 0)
            .Take(1);
}
