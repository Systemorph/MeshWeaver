using System.Text;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// <see cref="TextWriter"/> that buffers per line and flushes each completed
/// line to an <see cref="ILogger"/> as a separate log entry at
/// <paramref name="level"/> (<see cref="LogLevel.Information"/> for the stdout
/// pipe, <see cref="LogLevel.Error"/> for the stderr pipe). Used by
/// <see cref="KernelExecutor"/> to route a script's <c>Console.Write*</c> /
/// <c>Console.Error.Write*</c> output into the script's <c>ActivityLog</c>,
/// in line with how <c>Log.LogInformation(...)</c> calls flow.
/// </summary>
internal sealed class LoggerTextWriter(ILogger logger, LogLevel level = LogLevel.Information) : TextWriter
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
        logger.Log(level, "{Stdout}", msg);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Flush();
        base.Dispose(disposing);
    }
}
