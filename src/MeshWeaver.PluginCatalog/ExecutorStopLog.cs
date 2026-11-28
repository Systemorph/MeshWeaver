using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The ONE way a control-plane executor reports that its step chain ended. The cause is UNWRAPPED
/// first — a reflective dispatch (<c>SynchronizationStream.Reduce</c>) once wrapped a hub disposal in a
/// <see cref="System.Reflection.TargetInvocationException"/>, which read as an unclassified defect.
/// The request node's own hub disposing (a restart, a recycle) is the documented way the chain
/// ends — the request keeps its status and the next activation resumes it — so it is Information;
/// anything else is a real fault and stays an Error naming the real exception.
/// </summary>
internal static class ExecutorStopLog
{
    /// <summary>Reports the stop.</summary>
    /// <param name="logger">The executor's logger; may be null.</param>
    /// <param name="ex">The fault that ended the chain.</param>
    /// <param name="address">The request node's hub address.</param>
    /// <param name="tag">The log prefix, e.g. <c>[Reboot]</c>.</param>
    /// <param name="consequence">What happens to the request, appended after the dash.</param>
    /// <param name="hubShuttingDown">Whether the executor's own hub is shutting down. A disposed
    /// CONTAINER names no hub, so it is only read as this hub's teardown when the hub is in fact
    /// shutting down; otherwise it may be another hub's and stays an Error.</param>
    internal static void Report(ILogger? logger, Exception ex, Address address, string tag, string consequence,
        bool hubShuttingDown = false)
    {
        var cause = ex;
        while (cause is System.Reflection.TargetInvocationException { InnerException: { } inner })
            cause = inner;
        if (FaultClassifier.IsHubDisposalOf(cause, address) || (hubShuttingDown && FaultClassifier.IsDisposedContainer(cause)))
            logger?.LogInformation(
                "{Tag} the executor on {Path} stopped because its hub is shutting down — {Consequence}",
                tag, address, consequence);
        else
            logger?.LogError(cause, "{Tag} the executor on {Path} stopped — {Consequence}",
                tag, address, consequence);
    }
}
