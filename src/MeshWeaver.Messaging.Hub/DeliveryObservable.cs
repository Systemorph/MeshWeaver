using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Messaging;

/// <summary>
/// Bridges a genuinely-async delivery handler into the reactive rule chain —
/// the same eager-pool + ReplaySubject pattern as
/// <c>MeshWeaver.Mesh.Threading.IoPool.Run</c> (which is NOT reachable here:
/// <c>IoPool</c> lives in <c>MeshWeaver.Mesh.Contract</c>, and that assembly
/// references this one). The async leaf is pushed onto the ThreadPool
/// immediately and its single result is replayed through a
/// <see cref="ReplaySubject{T}"/>, so the serialized actor-loop subscriber can
/// never deadlock on a continuation that captured its scheduler.
///
/// <para><b>AccessContext.</b> The eager <c>Subscribe</c> runs on the
/// action-block thread (where the rule fires, inside
/// <c>IMessageHub.HandleMessageAsync</c>'s AccessContext try/finally), and
/// <c>SubscribeOn(TaskPoolScheduler)</c> captures the ExecutionContext — which
/// carries the <c>AccessService</c> AsyncLocal identity — so the pooled work
/// runs under the originating user's identity. (Verified by the security suite.)</para>
///
/// <para>Synchronous handlers never come here — they project via
/// <c>Observable.Return</c>. Only handlers that genuinely <c>await</c> external
/// work (a user <c>ExecutionRequest.Action</c>, disposal, a route round-trip)
/// delegate to the pool.</para>
/// </summary>
internal static class DeliveryObservable
{
    public static IObservable<IMessageDelivery> Run(
        Func<CancellationToken, Task<IMessageDelivery>> io)
    {
        var subject = new ReplaySubject<IMessageDelivery>(1);
        Observable.FromAsync(io).SubscribeOn(TaskPoolScheduler.Default).Subscribe(subject);
        return subject.AsObservable();
    }
}
