using MeshWeaver.Data.Validation;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Security;

/// <summary>
/// Checks read access for hub subscriptions using the security service.
/// When RLS is enabled, SubscribeRequests are denied if the caller
/// doesn't have Permission.Read on the target hub's path.
/// </summary>
internal class RlsSubscriptionAccessChecker(
    ISecurityService securityService,
    AccessService accessService
) : ISubscriptionAccessChecker
{
    public async Task<(bool Allowed, string? ErrorMessage)> CheckReadAccessAsync(string hubPath, CancellationToken ct)
    {
        var hasRead = await securityService.HasPermissionAsync(hubPath, Permission.Read, ct);
        if (hasRead)
            return (true, null);

        var userId = accessService.Context?.ObjectId ?? "(anonymous)";
        return (false, $"User '{userId}' does not have Read permission on '{hubPath}'");
    }
}
