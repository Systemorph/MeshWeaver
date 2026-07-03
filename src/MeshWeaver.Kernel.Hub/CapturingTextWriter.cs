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
/// </summary>
internal sealed class CapturingTextWriter(AsyncLocal<TextWriter?> channel, TextWriter fallback) : TextWriter
{
    // AsyncLocal flow-state (NOT a cache/collection): scopes each capture to its own
    // logical execution context. One channel per standard stream.
    private static readonly AsyncLocal<TextWriter?> CurrentOutTarget = new();
    private static readonly AsyncLocal<TextWriter?> CurrentErrorTarget = new();
    private static int installedOut;
    private static int installedError;

    public override Encoding Encoding => fallback.Encoding;

    private TextWriter Active => channel.Value ?? fallback;

    public override void Write(char value) => Active.Write(value);
    public override void Write(string? value) => Active.Write(value);
    public override void Write(char[] buffer, int index, int count) => Active.Write(buffer, index, count);
    public override void WriteLine() => Active.WriteLine();
    public override void WriteLine(string? value) => Active.WriteLine(value);
    public override void Flush() => Active.Flush();

    /// <summary>
    /// Begin capturing <c>Console.Out</c> writes to <paramref name="outTarget"/> and
    /// <c>Console.Error</c> writes to <paramref name="errorTarget"/> on the current
    /// async-flow. Dispose the returned scope to restore the previous targets.
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
        CurrentOutTarget.Value = outTarget;
        CurrentErrorTarget.Value = errorTarget;
        return new Restorer(() =>
        {
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
