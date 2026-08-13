using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// Fluent bridges from an async / async-stream I/O leaf into an
/// <see cref="IObservable{T}"/> — the <b>only</b> sanctioned replacement for a
/// bare <c>Observable.FromAsync(...)</c> in adapter / provider code.
///
/// <para><b>Why not <c>Observable.FromAsync</c>.</b> <c>FromAsync(asyncFn).SubscribeOn(pool)</c>
/// only moves the <i>subscribe</i> onto the pool; the <c>await</c> continuations
/// inside <c>asyncFn</c> resume on whatever scheduler the awaited task captured.
/// When the leaf is consumed by a <b>blocking subscriber</b> — a hub/grain
/// ActionBlock, or a test's synchronous wait — that continuation can be queued
/// behind the very thread that is blocked waiting for it, and the leaf
/// deadlocks. This is the recurring "search/query hangs under load" failure.</para>
///
/// <para><b>The pattern.</b> The leaf is pushed onto the pool <i>immediately</i>
/// (every <c>await</c> runs <c>ConfigureAwait(false)</c> behind the pool's
/// concurrency gate — see <see cref="IoPool"/>) and its results are pumped into a
/// <see cref="ReplaySubject{T}"/>; callers subscribe to the subject. Because the
/// leaf never depends on the subscriber's thread to make progress, and the
/// subject buffers every emission, a blocking subscriber can attach late and
/// still observe the full result — it can never deadlock the leaf.</para>
/// </summary>
public static class IoPoolExtensions
{
    /// <summary>
    /// Runs a genuinely-async I/O leaf (DB round-trip, blob, async file) in the
    /// pool and replays its single result. Drop-in for
    /// <c>Observable.FromAsync(io).SubscribeOn(TaskPoolScheduler.Default)</c>.
    ///
    /// <para>🚨 Caching this observable for reuse? Use
    /// <see cref="PromiseCache{TKey,TValue}"/> / <see cref="PromiseSlot{TValue}"/>, never a bare
    /// <c>ConcurrentDictionary</c> or <c>_field ??= …</c>: the <see cref="ReplaySubject{T}"/>
    /// below latches <c>OnError</c> as readily as a value, so an un-evicted cache turns ONE
    /// transient fault into a permanent one (#1369).</para>
    /// </summary>
    public static IObservable<T> Run<T>(this IIoPool pool, Func<CancellationToken, Task<T>> io)
    {
        var subject = new ReplaySubject<T>();
        // Eager: the work is scheduled on the pool NOW, decoupled from whoever
        // subscribes to the returned observable. The inner subscription auto-
        // disposes when the (completing) leaf calls OnCompleted.
        pool.Invoke(io).Subscribe(subject);
        return subject.AsObservable();
    }

    /// <summary>
    /// Runs a sync-blocking / CPU leaf (<c>File.Exists</c> probe, a Roslyn compile) in the pool
    /// and replays its single result — <see cref="Run{T}"/>'s counterpart for
    /// <see cref="IIoPool.InvokeBlocking{T}"/>, so a one-shot over a blocking leaf never has to
    /// hand-roll its own <see cref="ReplaySubject{T}"/>.
    /// </summary>
    public static IObservable<T> RunBlocking<T>(this IIoPool pool, Func<CancellationToken, T> work)
    {
        var subject = new ReplaySubject<T>();
        pool.InvokeBlocking(work).Subscribe(subject);
        return subject.AsObservable();
    }

    /// <summary>
    /// Runs an <see cref="IAsyncEnumerable{T}"/> leaf in the pool and replays
    /// every item. Drop-in for
    /// <c>Observable.FromAsync(async ct => { await foreach ... })</c>.
    /// </summary>
    public static IObservable<T> RunStream<T>(this IIoPool pool, Func<CancellationToken, IAsyncEnumerable<T>> source)
    {
        var subject = new ReplaySubject<T>();
        pool.InvokeStream(source).Subscribe(subject);
        return subject.AsObservable();
    }
}
