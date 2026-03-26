namespace MeshWeaver.Mesh;

/// <summary>
/// Tracks the latest query request and cancels superseded ones.
/// Use one instance per UI component / session to ensure only the most recent
/// autocomplete or search request runs to completion.
/// Thread-safe: multiple calls to <see cref="StartNew"/> safely cancel previous requests.
/// </summary>
public class QueryContext : IDisposable
{
    private CancellationTokenSource? _currentCts;

    /// <summary>
    /// Starts a new query, cancelling any in-flight previous query.
    /// Returns a CancellationToken that is cancelled when the next query starts
    /// or when the external token is cancelled.
    /// </summary>
    public CancellationToken StartNew(CancellationToken externalToken = default)
    {
        var newCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var previous = Interlocked.Exchange(ref _currentCts, newCts);
        try { previous?.Cancel(); } catch { /* already disposed */ }
        previous?.Dispose();
        return newCts.Token;
    }

    /// <summary>
    /// Cancels any in-flight query and releases the current cancellation token source.
    /// </summary>
    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _currentCts, null);
        try { cts?.Cancel(); } catch { /* already disposed */ }
        cts?.Dispose();
    }
}
