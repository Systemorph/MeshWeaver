using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Incoming grain call filter that logs the user identity propagated via Orleans RequestContext.
/// The client side sets "UserId" and "UserName" in RequestContext before each grain call.
/// This filter reads them on the silo side for diagnostics and verification.
/// </summary>
public class AccessContextGrainCallFilter(ILogger<AccessContextGrainCallFilter> logger)
    : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        try
        {
            var userId = RequestContext.Get("UserId") as string;
            var userName = RequestContext.Get("UserName") as string;

            // Skip Orleans system grains — they don't carry user identity and
            // they fire continuously (memorystreamqueue Dequeue, sys.svc.stream.agent
            // InvokeCallbackAsync — 8 agents × ~10 polls/sec = 80+ log lines/sec
            // at Debug, drowning out the application traces this filter exists to
            // expose. Application grains start with a node-path prefix (e.g.
            // `rbuergi/...`); system grains start with `sys.` or `memorystreamqueue`.
            if (logger.IsEnabled(LogLevel.Debug) && !IsSystemGrain(context))
            {
                logger.LogDebug(
                    "GrainCallFilter: grain={Grain}, method={Method}, userId={UserId}, userName={UserName}",
                    context.TargetId,
                    context.MethodName,
                    userId ?? "(none)",
                    userName ?? "(none)");
            }

            await context.Invoke();
        }
        catch (NullReferenceException) when (context.MethodName is "Stop" or "Close")
        {
            // Orleans internal streaming grains (PersistentStreamPullingManager) can throw
            // NullReferenceException during shutdown. Swallow to prevent test/startup failures.
            logger.LogDebug("GrainCallFilter: swallowed NullReferenceException in {Method} on {Grain}",
                context.MethodName, context.TargetId);
        }
    }

    /// <summary>
    /// True for Orleans system grains that fire continuously and don't carry
    /// user identity — memory-stream pulling agents, queue grains, lifecycle
    /// grains. Filter skips logging these to keep Debug traces readable.
    /// </summary>
    private static bool IsSystemGrain(IIncomingGrainCallContext context)
    {
        var targetId = context.TargetId.ToString();
        return targetId.StartsWith("sys.", StringComparison.Ordinal)
            || targetId.StartsWith("memorystreamqueue/", StringComparison.Ordinal)
            || targetId.StartsWith("manifestsystemtarget/", StringComparison.Ordinal);
    }
}
