using System.Text;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// Process-wide <c>Console.Out</c> / <c>Console.Error</c> replacement that delegates
/// writes to an <see cref="AsyncLocal{T}"/> target when one is set, falling back to
/// the original writer otherwise.
///
/// <para>Why AsyncLocal: the kernel runs scripts on the thread-pool with async
/// continuations. <c>Console.SetOut</c>/<c>Console.SetError</c> are process-global,
/// so naively swapping them around a single script execution would either (a) leak
/// captured output from concurrent kernel sessions into the wrong cell, or (b) leave
/// the console writers permanently broken if two captures interleaved. AsyncLocal
/// scopes the target to the logical execution context — each script's continuations
/// see only their own writer, and host-process code outside any capture scope hits
/// the fallback unchanged.</para>
///
/// <para>Both standard streams are hooked: <c>Console.WriteLine</c> lands on the
/// capture's stdout target, <c>Console.Error.WriteLine</c> on its stderr target —
/// so a script's error prints reach the ActivityLog (as error-level messages)
/// instead of vanishing into the host process's stderr.</para>
///
/// <para>🚨 The AsyncLocal holds a mutable <see cref="CaptureCell"/>, NEVER the
/// writer itself. Anything that snapshots the ExecutionContext while a capture is
/// active — a lazily-created long-lived <c>TimerQueueTimer</c>, a pending Rx delay,
/// a pooled work item — freezes the AsyncLocal map into that snapshot, and the
/// scope's Restorer can never reach it. When the map held the
/// <c>LoggerTextWriter</c> directly, one such timer pinned the writer →
/// <c>ActivityLogLogger</c> → the DISPOSED kernel activity hub → the whole mesh for
/// the timer's lifetime (the <c>MeshHubDisposalLeakTest</c> CI retention path, run
/// 30068597014). With the cell indirection, scope disposal nulls
/// <see cref="CaptureCell.Target"/> and SEVERS the graph even inside frozen
/// snapshots — a stray context keeps only the empty cell.</para>
/// </summary>
internal sealed class CapturingTextWriter(AsyncLocal<CapturingTextWriter.CaptureCell?> channel, TextWriter fallback) : TextWriter
{
    /// <summary>
    /// Indirection between the AsyncLocal map and the capture target. Captured
    /// ExecutionContexts reference this cell; disposing the capture scope nulls
    /// <see cref="Target"/>, releasing the writer graph from every snapshot.
    /// </summary>
    internal sealed class CaptureCell
    {
        public volatile TextWriter? Target;
    }

    // AsyncLocal flow-state (NOT a cache/collection): scopes each capture to its own
    // logical execution context. One channel per standard stream.
    private static readonly AsyncLocal<CaptureCell?> CurrentOutTarget = new();
    private static readonly AsyncLocal<CaptureCell?> CurrentErrorTarget = new();
    private static int installedOut;
    private static int installedError;

    public override Encoding Encoding => fallback.Encoding;

    private TextWriter Active => channel.Value?.Target ?? fallback;

    public override void Write(char value) => Active.Write(value);
    public override void Write(string? value) => Active.Write(value);
    public override void Write(char[] buffer, int index, int count) => Active.Write(buffer, index, count);
    public override void WriteLine() => Active.WriteLine();
    public override void WriteLine(string? value) => Active.WriteLine(value);
    public override void Flush() => Active.Flush();

    /// <summary>
    /// Begin capturing <c>Console.Out</c> writes to <paramref name="outTarget"/> and
    /// <c>Console.Error</c> writes to <paramref name="errorTarget"/> on the current
    /// async-flow. Dispose the returned scope to restore the previous targets AND
    /// sever the targets from any ExecutionContext snapshotted during the scope
    /// (see the class remarks — this is what keeps disposed meshes collectible).
    /// Idempotently installs the global console hooks on first use.
    /// </summary>
    public static IDisposable Capture(TextWriter outTarget, TextWriter errorTarget)
    {
        if (Interlocked.CompareExchange(ref installedOut, 1, 0) == 0)
            Console.SetOut(new CapturingTextWriter(CurrentOutTarget, Console.Out));
        if (Interlocked.CompareExchange(ref installedError, 1, 0) == 0)
            Console.SetError(new CapturingTextWriter(CurrentErrorTarget, Console.Error));

        var prevOut = CurrentOutTarget.Value;
        var prevError = CurrentErrorTarget.Value;
        var outCell = new CaptureCell { Target = outTarget };
        var errorCell = new CaptureCell { Target = errorTarget };
        CurrentOutTarget.Value = outCell;
        CurrentErrorTarget.Value = errorCell;
        return new Restorer(() =>
        {
            // Sever FIRST: contexts already captured by timers / pool items during
            // the scope hold these cells — nulling Target releases the writer graph
            // inside those frozen snapshots (their late writes fall back to the real
            // console instead of a disposed pipe). Then restore the current flow.
            outCell.Target = null;
            errorCell.Target = null;
            CurrentOutTarget.Value = prevOut;
            CurrentErrorTarget.Value = prevError;
        });
    }

    private sealed class Restorer(Action restore) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) restore();
        }
    }
}
