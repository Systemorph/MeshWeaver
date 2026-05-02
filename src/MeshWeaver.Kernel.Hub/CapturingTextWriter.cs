using System.Text;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// Process-wide <c>Console.Out</c> replacement that delegates writes to an
/// <see cref="AsyncLocal{T}"/> target when one is set, falling back to the
/// original writer otherwise.
///
/// <para>Why AsyncLocal: the kernel runs scripts on the thread-pool with async
/// continuations. <c>Console.SetOut</c> is process-global, so naively swapping
/// it around a single script execution would either (a) leak captured output
/// from concurrent kernel sessions into the wrong cell, or (b) leave Console.Out
/// permanently broken if two captures interleaved. AsyncLocal scopes the target
/// to the logical execution context — each script's continuations see only
/// their own writer, and host-process code outside any capture scope hits the
/// fallback unchanged.</para>
/// </summary>
internal sealed class CapturingTextWriter(TextWriter fallback) : TextWriter
{
    private static readonly AsyncLocal<TextWriter?> CurrentTarget = new();
    private static int installed;

    public override Encoding Encoding => fallback.Encoding;

    private TextWriter Active => CurrentTarget.Value ?? fallback;

    public override void Write(char value) => Active.Write(value);
    public override void Write(string? value) => Active.Write(value);
    public override void Write(char[] buffer, int index, int count) => Active.Write(buffer, index, count);
    public override void WriteLine() => Active.WriteLine();
    public override void WriteLine(string? value) => Active.WriteLine(value);
    public override void Flush() => Active.Flush();

    /// <summary>
    /// Begin capturing writes to <paramref name="target"/> on the current async-flow.
    /// Dispose the returned scope to restore the previous target. Idempotently
    /// installs the global Console.Out hook on first use.
    /// </summary>
    public static IDisposable Capture(TextWriter target)
    {
        if (Interlocked.CompareExchange(ref installed, 1, 0) == 0)
            Console.SetOut(new CapturingTextWriter(Console.Out));

        var prev = CurrentTarget.Value;
        CurrentTarget.Value = target;
        return new Restorer(() => CurrentTarget.Value = prev);
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
