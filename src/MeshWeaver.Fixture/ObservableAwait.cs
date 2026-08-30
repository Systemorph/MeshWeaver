using MeshWeaver.Messaging;

namespace MeshWeaver.Fixture;

/// <summary>
/// The test tree's <c>Await</c> — now a thin forward to <see cref="MeshWeaver.Messaging.ObservableAwait"/>,
/// which moved into a PRODUCT assembly so the code that actually deadlocks a hub (SDK-boundary
/// <c>…Async</c> overrides we must implement) has the same sanctioned bridge the tests do.
///
/// <para>Kept as a forward rather than deleted so the ~1,500 test call sites need no churn, and so
/// there is exactly ONE implementation of the semantics the sweep depends on — last value, faults
/// on an empty sequence, and a continuation that is QUEUED rather than resumed inline.</para>
/// </summary>
public static class ObservableAwait
{
    /// <inheritdoc cref="MeshWeaver.Messaging.ObservableAwait.Await{T}(IObservable{T}, CancellationToken)" />
    public static Task<T> Await<T>(this IObservable<T> source, CancellationToken cancellationToken = default)
        => MeshWeaver.Messaging.ObservableAwait.Await(source, cancellationToken);
}
