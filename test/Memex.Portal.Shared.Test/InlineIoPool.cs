using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Threading;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// An <see cref="IIoPool"/> that runs blocking work INLINE on the subscribing thread.
///
/// <para>🚨 It is not a stand-in for the production pool and must never be used to assert anything
/// about scheduling — using it to "prove" that a probe does not block would prove the opposite of
/// what it looks like. It exists so a test ABOUT something else (how many questions a reading asks
/// of the volume) does not become a test about when a pool thread was scheduled.</para>
///
/// <para>Only <see cref="InvokeBlocking{T}"/> is implemented. The other leaves throw rather than
/// returning something plausible: a test whose subject starts using one of them should fail loudly
/// here rather than silently measure a different mechanism.</para>
/// </summary>
internal sealed class InlineIoPool : IIoPool
{
    /// <summary>The shared instance — it holds no state.</summary>
    public static readonly InlineIoPool Instance = new();

    private InlineIoPool()
    {
    }

    /// <inheritdoc />
    public IObservable<T> InvokeBlocking<T>(Func<CancellationToken, T> work)
        => Observable.Defer(() => Observable.Return(work(CancellationToken.None)));

    /// <inheritdoc />
    public int CurrentInFlight => 0;

    /// <inheritdoc />
    public IObservable<T> Invoke<T>(Func<CancellationToken, Task<T>> io)
        => throw new NotSupportedException(
            "InlineIoPool runs blocking leaves only — see the type remarks.");

    /// <inheritdoc />
    public IObservable<T> InvokeStream<T>(Func<CancellationToken, IAsyncEnumerable<T>> source)
        => throw new NotSupportedException(
            "InlineIoPool runs blocking leaves only — see the type remarks.");

    /// <inheritdoc />
    public IObservable<T> SubscribeThroughPool<T>(IObservable<T> source)
        => throw new NotSupportedException(
            "InlineIoPool runs blocking leaves only — see the type remarks.");
}
