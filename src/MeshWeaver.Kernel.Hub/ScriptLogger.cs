using Microsoft.Extensions.Logging;

namespace MeshWeaver.Kernel.Hub;

/// <summary>
/// Forwarding <see cref="ILogger"/> whose target is swappable. Used as the
/// <c>Log</c> global on <see cref="MeshScriptGlobals"/> so the kernel can
/// rebind it per submission (e.g. to point at a request-supplied ActivityLog)
/// without rebuilding the script's globals object — Roslyn binds globals once
/// at first <c>RunAsync</c>, so the instance reference must stay stable.
/// </summary>
internal sealed class ScriptLogger(ILogger initial) : ILogger
{
    private ILogger inner = initial;

    public void Set(ILogger newInner) => inner = newInner;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => inner.Log(logLevel, eventId, state, exception, formatter);
}
