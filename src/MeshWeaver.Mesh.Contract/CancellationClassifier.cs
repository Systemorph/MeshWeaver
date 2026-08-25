namespace MeshWeaver.Mesh;

/// <summary>
/// Tells a <b>cancellation</b> apart from a <b>fault</b> — the one distinction a node-operation
/// handler's catch-all cannot make on its own, and the reason routine teardown used to be logged and
/// ticketed as if a write had failed.
///
/// <para>🚨 <b>Why this is not a log-level preference</b> (#2152, #2182). A cooperative cancellation
/// is a control-flow signal: the caller went away, the hub is tearing down, the host is shutting
/// down, the I/O pool was drained. Nothing failed, nothing is inconsistent, and there is nothing to
/// investigate — but `fail:` on that outcome is indistinguishable from a real storage outage on the
/// same path, so on-call reads 491 cancellations and the one genuine failure looks exactly like
/// them. Naming the condition is what makes the remaining `fail:` lines mean something again.</para>
///
/// <para>🚨 <b>It is NOT "catch OperationCanceledException".</b> A timeout implemented on a token
/// throws the same type and IS a fault — an availability failure someone must look at. .NET marks
/// that case by hanging a <see cref="TimeoutException"/> off the cancellation (an
/// <c>HttpClient</c> timeout is verbatim
/// <c>TaskCanceledException("… the configured HttpClient.Timeout of 100 s elapsed", new TimeoutException())</c>),
/// which is exactly what this refuses to call benign.</para>
/// </summary>
public static class CancellationClassifier
{
    /// <summary>
    /// True when <paramref name="exception"/> is a token firing because someone cancelled it —
    /// never a timeout, never a wrapped fault.
    /// </summary>
    /// <param name="exception">The exception a handler's error branch received.</param>
    /// <returns>True for cooperative cancellation; false for everything else, including a
    /// cancellation raised to express a timeout.</returns>
    public static bool IsCooperativeCancellation(Exception? exception) =>
        Unwrap(exception) is OperationCanceledException cancelled && !HasTimeoutCause(cancelled);

    /// <summary>
    /// The evidence line for a benign cancellation — what token state the exception actually
    /// carried, so the Debug line says WHY it was judged benign instead of asserting it.
    /// </summary>
    /// <param name="exception">The exception a handler's error branch received.</param>
    /// <returns>A short, log-safe description.</returns>
    public static string Describe(Exception? exception) =>
        Unwrap(exception) is OperationCanceledException cancelled
            ? $"{cancelled.GetType().Name} (token cancelled: "
              + $"{cancelled.CancellationToken.IsCancellationRequested.ToString().ToLowerInvariant()})"
            : exception?.GetType().Name ?? "(none)";

    /// <summary>
    /// Reactive operators surface the original exception, but a bridged <c>Task</c> can still hand
    /// over a single-inner <see cref="AggregateException"/>. Look through exactly that one wrapper —
    /// a multi-inner aggregate is genuinely several faults and stays one.
    /// </summary>
    private static Exception? Unwrap(Exception? exception) =>
        exception is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? aggregate.InnerExceptions[0]
            : exception;

    /// <summary>
    /// True when the cancellation was raised to express a timeout — the impostor this classifier
    /// exists to keep out. Walks the inner chain because a transport may wrap it once more.
    /// </summary>
    private static bool HasTimeoutCause(Exception exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            if (inner is TimeoutException)
                return true;
        return false;
    }
}
