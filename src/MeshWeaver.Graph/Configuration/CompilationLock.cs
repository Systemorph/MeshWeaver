using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// File-based lock for multi-process compilation synchronization.
/// Uses FileShare.None with FileOptions.DeleteOnClose for automatic cleanup.
/// </summary>
internal sealed class CompilationLock : IDisposable
{
    private FileStream? _lockStream;

    private CompilationLock()
    {
    }

    /// <summary>
    /// Acquires a file-based lock for the given node name.
    /// Uses exponential backoff when the lock is held by another process.
    /// </summary>
    /// <param name="lockDirectory">Directory where lock files are stored.</param>
    /// <param name="nodeName">Sanitized node name (used as lock file base name).</param>
    /// <param name="timeout">Maximum time to wait for the lock.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A CompilationLock that should be disposed when done.</returns>
    /// <exception cref="TimeoutException">Thrown when lock cannot be acquired within timeout.</exception>
    public static async Task<CompilationLock> AcquireAsync(
        string lockDirectory,
        string nodeName,
        TimeSpan timeout,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var lockPath = Path.Combine(lockDirectory, $"{nodeName}.lock");
        Directory.CreateDirectory(lockDirectory);

        var stopwatch = Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(50);
        const double backoffMultiplier = 1.5;
        const int maxDelayMs = 2000;

        while (stopwatch.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // FileShare.None ensures exclusive access
                // FileOptions.DeleteOnClose cleans up lock file when stream is closed
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);

                logger?.LogDebug("Acquired compilation lock for {NodeName}", nodeName);
                return new CompilationLock { _lockStream = stream };
            }
            catch (IOException)
            {
                // Lock is held by another process, wait and retry
                logger?.LogDebug(
                    "Lock held for {NodeName}, waiting {Delay}ms (elapsed: {Elapsed}ms)",
                    nodeName, delay.TotalMilliseconds, stopwatch.ElapsedMilliseconds);

                await Task.Delay(delay, ct);

                // Exponential backoff with max cap
                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * backoffMultiplier, maxDelayMs));
            }
        }

        throw new TimeoutException(
            $"Failed to acquire compilation lock for '{nodeName}' within {timeout.TotalSeconds}s. " +
            "Another process may be compiling the same node.");
    }

    /// <summary>
    /// Releases the lock by closing the file stream.
    /// The lock file is automatically deleted due to FileOptions.DeleteOnClose.
    /// </summary>
    public void Dispose()
    {
        _lockStream?.Dispose();
        _lockStream = null;
    }
}
