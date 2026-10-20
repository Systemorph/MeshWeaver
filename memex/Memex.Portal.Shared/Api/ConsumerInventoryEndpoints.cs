using System.Reactive.Linq;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// <c>POST /api/instances/inventory</c> — a consumer delivers its <see cref="DeploymentReport"/> to
/// the plugin registry it pulls from (#6989).
///
/// <para>A registry serves each consumer bundles keyed on the framework identity that consumer runs
/// and adopts, out of the registry's OWN prebuilt store, and the store's retention may not delete an
/// identity a consumer still uses. Its expected consumers are the <c>Hosting/Deployment</c> records
/// that mount it; until this route existed their reports all went to the control instance, so the
/// registry's retention refused on every pass and the shared volume was never reclaimed.</para>
///
/// <para>🚨 <b>The caller's registry credential is the whole authorization, and it decides the
/// key.</b> The report is filed under the instance id the credential AUTHENTICATED as
/// (<see cref="DeploymentReport.RegisteredInstance"/>), never under the deployment the body names:
/// a report can only protect MORE identities, and the one way it could do harm — making another
/// instance's record read as freshly reported — is closed by binding the key to the sender. The
/// write itself runs as System (<see cref="DeploymentReportService.FileStoreReport"/>), after the
/// check. Mapped by <c>MapInstanceRegistration</c>, so every host that registers instances accepts
/// their reports.</para>
/// </summary>
public static class ConsumerInventoryEndpoints
{
    /// <summary>Maps the consumer-report route.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapConsumerInventory(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(DeploymentReportService.StoreInboxRoute, Receive).AllowAnonymous();
        return endpoints;
    }

    private static Task<IResult> Receive(
        HttpContext http, IMessageHub rootHub, DeploymentReport? body, CancellationToken ct)
    {
        var logger = http.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(ConsumerInventoryEndpoints));
        if (body is null || !string.Equals(body.Event, DeploymentReportService.EventName, StringComparison.Ordinal))
            return Task.FromResult(Results.Json(
                new { error = $"the body must be a module inventory report (event: {DeploymentReportService.EventName})." },
                statusCode: StatusCodes.Status400BadRequest));

        var authenticator = http.RequestServices.GetRequiredService<InstanceRegistryAuthenticator>();
        return authenticator.AuthenticateOutcome(http.Request.Headers.Authorization.ToString())
            .SelectMany(outcome =>
            {
                if (outcome.IsUnavailable)
                    return Observable.Return(InstanceAuthResponses.Unavailable(http, outcome.UnavailableReason, logger));
                if (outcome.Instance?.Instance.InstanceId is not { Length: > 0 } instanceId)
                    return Observable.Return(Results.Json(
                        new { error = "A registered instance key (or its token) is required. Nothing was filed." },
                        statusCode: StatusCodes.Status401Unauthorized));
                return DeploymentReportService.FileStoreReport(rootHub, instanceId, body)
                    .Do(path => logger?.LogInformation(
                        "Consumer report from registered instance {InstanceId} (deployment {Deployment}) filed at {Path}",
                        instanceId, body.Deployment, path))
                    .Select(path => Results.Json(new { path, registeredInstance = instanceId }));
            })
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex, "Consumer report could not be filed");
                return Observable.Return(Results.Json(
                    new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway));
            })
            .FirstAsync()
            .ObserveCompletion(
                ex => logger?.LogWarning(ex, "Consumer report faulted after the response had already been sent"),
                ct)!;
    }
}
