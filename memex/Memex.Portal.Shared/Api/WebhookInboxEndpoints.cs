using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Api;

/// <summary>
/// The generic webhook inbox endpoint: <c>POST /api/hooks/{**target}</c> stores the raw delivery
/// as a <c>WebhookEvent</c> node at <c>{target}/_Inbox/{id}</c> (see
/// <see cref="WebhookInbox"/>). Anonymous by design — external services (Stripe, GitHub, …)
/// cannot authenticate — and fail-closed: only targets allowlisted under
/// <c>WebhookInbox:Targets</c> in configuration accept deliveries; everything else is 404. The
/// endpoint verifies NOTHING about the payload beyond a size cap — signature verification is the
/// consuming plugin's job over the verbatim stored body + headers, so no integration-specific
/// (e.g. payment) code lives in the portal.
/// </summary>
public static class WebhookInboxEndpoints
{
    /// <summary>Maps the anonymous <c>/api/hooks/{**target}</c> inbox endpoint. Call alongside
    /// <c>MapMeshApi</c>.</summary>
    public static IEndpointRouteBuilder MapWebhookInbox(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/hooks/{**target}",
                (string target, HttpRequest request, IMessageHub rootHub, IConfiguration config,
                        CancellationToken ct) =>
                    Deliver(target, request, rootHub, config, ct))
            .AllowAnonymous();
        return endpoints;
    }

    // The sanctioned Task boundary (a minimal-API handler, like the MCP/registry adapters):
    // the body is reactive — read, deliver, map to a status code.
    private static async Task<IResult> Deliver(
        string target, HttpRequest request, IMessageHub hub, IConfiguration config,
        CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(WebhookInboxEndpoints));
        var allowed = config.GetSection(WebhookInbox.TargetsConfigSection).GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();

        // Refuse oversized bodies BEFORE buffering them (Content-Length first; the capped reader
        // below still guards chunked bodies that lie about their size).
        if (request.ContentLength is > WebhookInbox.MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        string body;
        using (var reader = new StreamReader(request.Body))
            body = await reader.ReadToEndAsync(ct);

        var headers = request.Headers.Select(h =>
            new KeyValuePair<string, string>(h.Key, h.Value.ToString()));

        var result = await WebhookInbox.Deliver(
                hub, allowed, target, request.ContentType, headers, body)
            .FirstAsync().ToTask(ct);
        switch (result.Status)
        {
            case WebhookInbox.DeliveryStatus.Accepted:
                logger?.LogInformation("Webhook stored at {Path}", result.NodePath);
                return Results.Ok();
            case WebhookInbox.DeliveryStatus.TooLarge:
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            default:
                // Unknown target: no detail leaks about which paths exist or are allowlisted.
                logger?.LogWarning("Webhook for unknown/refused target '{Target}' dropped", target);
                return Results.NotFound();
        }
    }
}
