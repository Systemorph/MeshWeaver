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
        var userId = RequestContext.Get("UserId") as string;
        var userName = RequestContext.Get("UserName") as string;

        if (logger.IsEnabled(LogLevel.Debug))
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
}
