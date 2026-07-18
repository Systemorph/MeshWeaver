using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// One received webhook delivery, stored verbatim as a node under the target's <c>_Inbox</c>
/// satellite (<c>{target}/_Inbox/{id}</c>). The inbox is deliberately DUMB: the endpoint verifies
/// nothing but the target allowlist and a size cap — signature verification (e.g. Stripe's
/// <c>Stripe-Signature</c> HMAC) is the CONSUMER's job, over the verbatim <see cref="Body"/> +
/// <see cref="Headers"/> stored here. A consumer watches its inbox with a mesh query and deletes
/// (or marks) processed events.
/// </summary>
public record WebhookEvent
{
    /// <summary>When the delivery was received (UTC).</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>The request's Content-Type.</summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// The request headers, verbatim (multi-values joined with <c>", "</c>) — minus credentials
    /// (<c>Authorization</c>, <c>Cookie</c>, …, see <see cref="WebhookInbox.DropHeader"/>).
    /// Signature headers (<c>Stripe-Signature</c>, <c>X-Hub-Signature-256</c>, …) are preserved so
    /// the consumer can verify authenticity.
    /// </summary>
    public ImmutableDictionary<string, string> Headers { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>The raw request body, verbatim — the exact bytes signatures are computed over
    /// (as UTF-8 text).</summary>
    public string Body { get; init; } = "";
}

/// <summary>
/// The generic webhook inbox: <c>POST /api/hooks/{target}</c> stores the raw delivery as a
/// <see cref="WebhookEvent"/> node at <c>{target}/_Inbox/{id}</c>. Commerce-free and fail-closed:
/// only targets explicitly allowlisted in configuration (<c>WebhookInbox:Targets</c>) accept
/// deliveries, the target node must exist (a satellite must anchor under a real owner), and the
/// body is capped. The portal maps the HTTP endpoint; this class holds the (testable) delivery
/// logic and the node-type registration.
/// </summary>
public static class WebhookInbox
{
    /// <summary>The node-type identifier for received webhook deliveries.</summary>
    public const string NodeType = "WebhookEvent";

    /// <summary>The satellite container deliveries land in.</summary>
    public const string InboxContainer = "_Inbox";

    /// <summary>The configuration section listing the node paths allowed to receive deliveries
    /// (e.g. <c>WebhookInbox:Targets:0 = Store/Payments</c>). Empty/missing = everything refused.</summary>
    public const string TargetsConfigSection = "WebhookInbox:Targets";

    /// <summary>Maximum accepted body size (bytes). Webhook events are small; anything bigger is
    /// refused with 413.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    /// <summary>The outcome of a delivery attempt — maps 1:1 onto the HTTP status the endpoint
    /// returns.</summary>
    public enum DeliveryStatus
    {
        /// <summary>Stored; the consumer will process it.</summary>
        Accepted,

        /// <summary>The target is not allowlisted or its node does not exist → 404.</summary>
        UnknownTarget,

        /// <summary>The body exceeds <see cref="MaxBodyBytes"/> → 413.</summary>
        TooLarge,
    }

    /// <summary>The result of <see cref="Deliver"/>: the status and, when accepted, the stored
    /// node's path.</summary>
    public record DeliveryResult(DeliveryStatus Status, string? NodePath = null);

    /// <summary>Registers the WebhookEvent node type on the mesh builder.</summary>
    public static TBuilder AddWebhookInbox<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.WithMeshType<WebhookEvent>();
        return builder;
    }

    /// <summary>Builds the MeshNode definition for the WebhookEvent node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Webhook Event",
        NodeType = "NodeType",
        Icon = "/static/NodeTypeIcons/satellite.svg",
        HubConfiguration = config => config
            .AddDefaultLayoutAreas()
            .AddMeshDataSource(source => source.WithContentType<WebhookEvent>())
    };

    /// <summary>Whether a header carries credentials and must never be persisted. Signature
    /// headers are NOT dropped — the consumer needs them to verify authenticity.</summary>
    public static bool DropHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase);

    /// <summary>Canonical form of a target path: no leading/trailing slashes. Null when the shape
    /// is not a plain node path (empty, or contains a <c>..</c> segment).</summary>
    public static string? NormalizeTarget(string? target)
    {
        var trimmed = (target ?? "").Trim().Trim('/');
        if (trimmed.Length == 0)
            return null;
        if (trimmed.Split('/').Any(seg => seg.Length == 0 || seg == ".."))
            return null;
        return trimmed;
    }

    /// <summary>
    /// Delivers one webhook to <paramref name="target"/>: the target must be in
    /// <paramref name="allowedTargets"/> AND its node must exist (fail-closed — a satellite must
    /// anchor under a real owner; an ownerless satellite NotFound-storms the router). The event is
    /// stored under the System identity (the anonymous caller has no write access anywhere — the
    /// allowlist is the authorization). Cold; never throws for a refused delivery — refusal is
    /// data.
    /// </summary>
    public static IObservable<DeliveryResult> Deliver(
        IMessageHub hub,
        IReadOnlyCollection<string> allowedTargets,
        string? target,
        string? contentType,
        IEnumerable<KeyValuePair<string, string>> headers,
        string body)
    {
        var normalized = NormalizeTarget(target);
        if (normalized is null
            || !allowedTargets.Any(t => string.Equals(
                NormalizeTarget(t), normalized, StringComparison.Ordinal)))
            return Observable.Return(new DeliveryResult(DeliveryStatus.UnknownTarget));
        if (System.Text.Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
            return Observable.Return(new DeliveryResult(DeliveryStatus.TooLarge));

        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Observable.Throw<DeliveryResult>(
                new InvalidOperationException("The mesh service is not available."));
        var accessService = hub.ServiceProvider.GetService<AccessService>();

        var kept = headers
            .Where(h => !DropHeader(h.Key))
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(
                g => g.Key,
                g => string.Join(", ", g.Select(h => h.Value)),
                StringComparer.OrdinalIgnoreCase);

        return Observable.Using(
                () => accessService?.ImpersonateAsSystem() ?? System.Reactive.Disposables.Disposable.Empty,
                _ => mesh
                    .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{normalized}")).Take(1)
                    .Select(c => c.Items.FirstOrDefault(n => n.Path == normalized))
                    .SelectMany(owner =>
                    {
                        if (owner is null)
                            return Observable.Return(new DeliveryResult(DeliveryStatus.UnknownTarget));
                        var id = Guid.NewGuid().ToString("N");
                        var node = new MeshNode(id, $"{normalized}/{InboxContainer}")
                        {
                            Name = $"Webhook {id}",
                            NodeType = NodeType,
                            MainNode = normalized,
                            Content = new WebhookEvent
                            {
                                ReceivedAt = DateTimeOffset.UtcNow,
                                ContentType = contentType,
                                Headers = kept,
                                Body = body,
                            },
                        };
                        return mesh.CreateNode(node).Take(1)
                            .Select(_ => new DeliveryResult(DeliveryStatus.Accepted, node.Path));
                    }));
    }
}
