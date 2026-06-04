using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Messaging;

/// <summary>
/// Bridges a genuinely-async delivery handler (one that internally <c>await</c>s
/// I/O — a user <c>ExecutionRequest.Action</c>, disposal) into the reactive rule
/// chain as a <see cref="ReplaySubject{T}"/> "promise".
///
/// <para><b>Single-threaded invariant.</b> There is deliberately NO
/// <c>SubscribeOn(TaskPoolScheduler)</c> here. The hub turn must stay on ONE
/// thread — every gratuitous pool hop is a thread transition, and under load
/// those transitions are the "near-miss" reordering that surfaces as the
/// request/response timeouts. The eager <c>Subscribe</c> runs the operation's
/// SYNCHRONOUS prefix inline on the action-block (turn) thread; only a genuine
/// <c>await</c> inside <paramref name="io"/> yields, and its continuation
/// resumes via the captured <see cref="System.Threading.ExecutionContext"/>
/// (which carries the <c>AccessService</c> AsyncLocal identity) — no extra
/// scheduler is interposed. The <see cref="ReplaySubject{T}"/> buffers the one
/// result so a later subscriber still observes it.</para>
///
/// <para>Synchronous handlers never come here — they project via
/// <c>Observable.Return</c> and complete inline on the turn thread.</para>
/// </summary>
internal static class DeliveryObservable
{
    public static IObservable<IMessageDelivery> Run(
        Func<CancellationToken, Task<IMessageDelivery>> io)
    {
        var subject = new ReplaySubject<IMessageDelivery>(1);
        // No SubscribeOn — stay on the turn thread; the sync prefix of `io` runs
        // inline, only a real await inside it yields.
        Observable.FromAsync(io).Subscribe(subject);
        return subject.AsObservable();
    }
}
