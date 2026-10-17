using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// ONE opening of a webhook-inbox drain: a live listing of deliveries, each claimed once and drained
/// one at a time. Shared by the inbox consumers that a hub watcher re-opens after a fault
/// (<c>ImageClosureIngest</c>, <c>RegistryUpdateReconciler</c>).
///
/// <para>🚨 <b>The listing and the drain queue are independent.</b> A <c>listing.SelectMany(…)
/// .Select(drain).Concat()</c> pipeline holds an error of the listing back until the drain in
/// flight completes, so a hung drain kept a FAULTED query from being re-opened at all. Here a
/// listing fault ends the opening at once, which disposes the drain in flight.</para>
///
/// <para>🚨 <b>A claim belongs to the opening that made it until its drain has run.</b> When the
/// opening ends (a listing fault, a teardown) every delivery it claimed and did not finish draining is
/// handed to <c>release</c>. Otherwise the re-opened watch would skip it as "already picked up" and it
/// would wait for the next boot.</para>
/// </summary>
internal static class InboxDrainWatch
{
    /// <summary>One opening; cold — the listing is opened on subscribe.</summary>
    /// <param name="openListing">Opens the live listing of the inbox.</param>
    /// <param name="claim">Claims the deliveries of one listing that are new to this process and
    /// returns them in drain order.</param>
    /// <param name="release">Gives back the claims of deliveries this opening did not finish.</param>
    /// <param name="drain">Drains one delivery. It must not fault — a failed drain is the caller's to
    /// log and complete.</param>
    /// <returns>One element per finished drain; faults only when the listing faults.</returns>
    public static IObservable<Unit> Open(
        Func<IObservable<IEnumerable<MeshNode>>> openListing,
        Func<IEnumerable<MeshNode>, IEnumerable<MeshNode>> claim,
        Action<ImmutableHashSet<string>> release,
        Func<MeshNode, IObservable<Unit>> drain) =>
        Observable.Create<Unit>(downstream =>
        {
            var observer = Observer.Synchronize(downstream);
            var unfinished = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
            var queue = new Subject<MeshNode>();
            var drains = queue
                .Select(node => Observable.Defer(() => drain(node))
                    // On COMPLETION only: a drain disposed mid-flight by the opening's end has not
                    // run, and must stay unfinished so it is released below.
                    .Do(_ => { }, () => ImmutableInterlocked.Update(ref unfinished, set => set.Remove(node.Path))))
                .Concat()
                .Subscribe(observer.OnNext, observer.OnError);
            var listing = Observable.Defer(openListing).Subscribe(
                nodes =>
                {
                    foreach (var node in claim(nodes ?? []))
                    {
                        ImmutableInterlocked.Update(ref unfinished, set => set.Add(node.Path));
                        queue.OnNext(node);
                    }
                },
                observer.OnError,
                observer.OnCompleted);
            return Disposable.Create(() =>
            {
                listing.Dispose();
                drains.Dispose();
                queue.Dispose();
                var released = Interlocked.Exchange(ref unfinished, unfinished.Clear());
                if (!released.IsEmpty)
                    release(released);
            });
        });
}
