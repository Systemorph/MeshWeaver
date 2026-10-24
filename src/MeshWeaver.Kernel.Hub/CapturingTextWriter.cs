using System.Text;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// Process-wide <c>Console.Out</c> / <c>Console.Error</c> replacement that delegates
/// writes to an <see cref="AsyncLocal{T}"/> target when one is set, falling back to
/// the process's own standard stream otherwise.
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
///
/// <para>🚨 The fallback is a writer of OUR OWN over the raw standard stream, NEVER the
/// <c>Console.Out</c> / <c>Console.Error</c> this hook replaces. <c>Console.SetOut</c> wraps the
/// hook in a synchronized writer, so every write through it already holds that wrapper's lock.
/// On Unix the console stream's write then takes <c>lock (Console.Out)</c>, which is the same
/// wrapper, so the lock is re-entered and no second lock is involved. Falling back THROUGH the
/// replaced writer added its lock as the second one, and anything that captured that writer
/// before the hook installed (xUnit's reporter, any host component that kept the startup
/// <c>Console.Out</c>) takes the two locks in the opposite order: it holds the replaced writer,
/// then needs <c>lock (Console.Out)</c>. That inversion deadlocked the
/// <c>MeshWeaver.Hosting.Orleans.Test</c> host on its own stdout until CI's 8-minute cap killed it.
/// Pinned by <c>ConsoleHookTakesNoLockOfTheWriterItReplacedTest</c>.</para>
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

    /// <summary>The writer <c>Console.Out</c> was when the hook installed — null until then.</summary>
    internal static TextWriter? ReplacedOut { get; private set; }

    /// <summary>The writer <c>Console.Error</c> was when the hook installed — null until then.</summary>
    internal static TextWriter? ReplacedError { get; private set; }

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
        {
            ReplacedOut = Console.Out;
            Console.SetOut(new CapturingTextWriter(CurrentOutTarget, RawStandardWriter(Console.OpenStandardOutput())));
        }
        if (Interlocked.CompareExchange(ref installedError, 1, 0) == 0)
        {
            ReplacedError = Console.Error;
            Console.SetError(new CapturingTextWriter(CurrentErrorTarget, RawStandardWriter(Console.OpenStandardError())));
        }

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

    /// <summary>
    /// The fallback for code outside any capture scope: an unsynchronized writer over the raw
    /// standard stream. It is only ever reached through the synchronized wrapper
    /// <c>Console.SetOut</c>/<c>SetError</c> puts around the hook, which serializes it — see the
    /// class remarks for why it must not be the writer the hook replaced.
    /// </summary>
    private static TextWriter RawStandardWriter(Stream stream) =>
        new StreamWriter(stream, WithoutPreamble(Console.OutputEncoding), bufferSize: 256, leaveOpen: true)
        {
            AutoFlush = true,
        };

    /// <summary>
    /// The console encoding without a byte-order mark: a <see cref="StreamWriter"/> over a
    /// non-seekable stream writes the preamble before its first write, which the console's own
    /// writer never does.
    /// </summary>
    private static Encoding WithoutPreamble(Encoding encoding) =>
        encoding.Preamble.Length == 0
            ? encoding
            : encoding switch
            {
                UTF8Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                UnicodeEncoding => new UnicodeEncoding(bigEndian: encoding.CodePage == 1201, byteOrderMark: false),
                UTF32Encoding => new UTF32Encoding(bigEndian: encoding.CodePage == 12001, byteOrderMark: false),
                _ => encoding,
            };

    private sealed class Restorer(Action restore) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) restore();
        }
    }
}
