using System.Reactive.Linq;
using MeshWeaver.Graph.ControlLane;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// The TARGET's control-lane endpoint: <c>POST /api/control-lane</c> hands one signed request to
/// <see cref="ControlLaneReceiver"/> and answers its verdict as the status code
/// (<see cref="ControlLaneVerdict.StatusCodeOf"/>) — 202 accepted, 401 bad signature, 403 wrong
/// deployment, 409 replay, 410 expired, 422 refused, 503 not armed. Anonymous by design: the
/// signature IS the authentication, verified before a single field is read (Doc/Architecture/ControlLane).
///
/// <para>An ACCEPTED answer's body is signed with the same key (<c>X-Hub-Signature-256</c>), so the
/// control instance can tell an acceptance by this deployment from a 2xx by anything else in the
/// path. Refusals are not signed: they carry no authority.</para>
/// </summary>
public static class ControlLaneEndpoints
{
    /// <summary>Maps <c>POST /api/control-lane</c>. Call alongside <c>MapWebhookInbox</c>.</summary>
    public static IEndpointRouteBuilder MapControlLane(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ControlLaneClient.EndpointRoute,
                (HttpRequest request, HttpResponse response, IMessageHub hub, IConfiguration config, CancellationToken ct) =>
                    Receive(request, response, hub, config, ct))
            .AllowAnonymous();
        return endpoints;
    }

    // The sanctioned Task boundary (a minimal-API handler): read the bounded body, receive, answer.
    private static async Task<IResult> Receive(
        HttpRequest request, HttpResponse response, IMessageHub hub, IConfiguration config, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(ControlLaneEndpoints));
        var receiver = hub.ServiceProvider.GetService<ControlLaneReceiver>();
        if (receiver is null)
        {
            logger?.LogError("[ControlLane] a delivery arrived but this host does not register the control lane (AddControlLane)");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if (request.ContentLength is > MeshWeaver.Graph.Configuration.WebhookInbox.MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        string? body;
        try
        {
            body = await BoundedBody.ReadAsync(request.Body, MeshWeaver.Graph.Configuration.WebhookInbox.MaxBodyBytes, ct);
        }
        catch (BadHttpRequestException ex)
        {
            return Results.StatusCode(ex.StatusCode);
        }
        if (body is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var signature = request.Headers[ControlLaneWire.SignatureHeader].ToString();
        var receipt = (await receiver.Receive(body, signature)
            .FirstAsync()
            .ObserveCompletion(
                ex => logger?.LogWarning(ex, "[ControlLane] a delivery faulted after its answer had already been sent"),
                ct))!;
        var answer = ControlLaneReceiver.Answer(receipt, config);
        if (answer.Signature is { } signed)
            response.Headers[ControlLaneWire.SignatureHeader] = signed;
        return Results.Content(answer.Body, "application/json", statusCode: answer.StatusCode);
    }
}
