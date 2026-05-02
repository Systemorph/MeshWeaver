using System.Text;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// <see cref="TextWriter"/> that buffers per line and flushes each completed
/// line to an <see cref="ILogger"/> as a separate <c>LogInformation</c> entry.
/// Used by <see cref="KernelExecutor"/> to route a script's <c>Console.Write*</c>
/// output into the script's <c>ActivityLog</c>, in line with how
/// <c>Log.LogInformation(...)</c> calls flow.
/// </summary>
internal sealed class LoggerTextWriter(ILogger logger) : TextWriter
{
    private readonly StringBuilder buffer = new();
    private readonly object bufferLock = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (bufferLock)
        {
            if (value == '\n')
            {
                FlushLocked();
            }
            else if (value != '\r')
            {
                buffer.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (var c in value) Write(c);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        for (var i = 0; i < count; i++) Write(buffer[index + i]);
    }

    public override void WriteLine() => Write('\n');
    public override void WriteLine(string? value)
    {
        if (value is not null) Write(value);
        Write('\n');
    }

    public override void Flush()
    {
        lock (bufferLock) FlushLocked();
    }

    private void FlushLocked()
    {
        if (buffer.Length == 0) return;
        var msg = buffer.ToString();
        buffer.Clear();
        logger.LogInformation("{Stdout}", msg);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Flush();
        base.Dispose(disposing);
    }
}
