using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
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
///
/// <para>🚨 A target MAY declare the configuration key holding its shared HMAC secret
/// (<see cref="SecretConfigKeyName"/>); when it does, the endpoint verifies
/// <see cref="SignatureHeader"/> over the raw body BEFORE storing, and answers 401 when it does
/// not verify. That is the whole of #3312: a 2xx used to mean "I received bytes", so a
/// MISMATCHED secret was indistinguishable from a correct one — the sender saw a green POST while
/// the consumer silently dropped every delivery as unverifiable, and nothing anywhere went red.
/// The answer now carries the VERDICT, so a publisher can fail because the record was not
/// ACCEPTED, not merely because it was not sent. A target that declares no key keeps the dumb
/// behaviour: schemes the endpoint does not speak (Stripe's <c>Stripe-Signature</c>) are still the
/// consumer's job over the verbatim body + headers stored here.</para>
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

    /// <summary>
    /// The child of a target entry naming the CONFIGURATION KEY that holds that target's shared
    /// HMAC secret — <c>WebhookInbox:Targets:0:SecretConfigKey = Hosting:PlatformWebhookSecret</c>
    /// (env: <c>WebhookInbox__Targets__0__SecretConfigKey</c>). It rides on the allowlist entry
    /// rather than in a parallel section on purpose: the record that makes a target reachable is
    /// the record that says how it is authenticated, so the two cannot drift apart. A section may
    /// carry both a value and children, so <c>Targets:0</c> still reads as the plain path for
    /// every existing consumer of the allowlist.
    ///
    /// <para>🚨 It names a KEY, never a secret. A value pasted here verbatim resolves to
    /// nothing and the target then refuses every delivery with
    /// <see cref="DeliveryStatus.SecretUnavailable"/> — loudly, in the fail-CLOSED direction —
    /// instead of landing a live credential in a ConfigMap.</para>
    /// </summary>
    public const string SecretConfigKeyName = "SecretConfigKey";

    /// <summary>The GitHub-style signature header verified for targets that declare a secret:
    /// <c>sha256=&lt;lowercase hex&gt;</c> of HMAC-SHA256 over the raw body.</summary>
    public const string SignatureHeader = "X-Hub-Signature-256";

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

        /// <summary>The target declares a secret and <see cref="SignatureHeader"/> is absent or
        /// does not verify over the raw body → 401. This is the state #3312 made visible: it used
        /// to be stored and answered 2xx, then dropped unverifiable by the consumer.</summary>
        SignatureInvalid,

        /// <summary>The target declares a secret config key that resolves to nothing → 500. A
        /// MISCONFIGURATION of this instance, not of the caller, and deliberately not 401: it must
        /// not read as "your secret is wrong". Fail-closed — nothing is stored.</summary>
        SecretUnavailable,
    }

    /// <summary>
    /// The result of <see cref="Deliver"/>: the status, the stored node's path when accepted, and
    /// whether the delivery was ACCEPTED HAVING VERIFIED a signature.
    ///
    /// <para>🚨 <see cref="SignatureVerified"/> is the half of #3312 the status code cannot
    /// carry. "Accepted, verified" and "accepted, nothing was required" are both 2xx and mean very
    /// different things to a sender that signed: the second says this instance declares no
    /// <see cref="SecretConfigKeyName"/> for the target, so its signature was never checked. A
    /// publisher reads THIS, not the status, to know whether its secret was actually exercised —
    /// otherwise "we verify now" degrades silently to "we used to verify" the day a chart value
    /// goes missing.</para>
    /// </summary>
    public record DeliveryResult(DeliveryStatus Status, string? NodePath = null)
    {
        /// <summary>Whether this delivery was accepted HAVING VERIFIED its signature. An
        /// init-only PROPERTY, not a third positional parameter: adding one to a record's primary
        /// constructor replaces the signature, and every assembly compiled against the old arity
        /// — every module compiled against the platform image — calls a constructor that no longer
        /// exists. `Public surface (binary compatibility)` refuses that, correctly.</summary>
        public bool SignatureVerified { get; init; }
    }

    /// <summary>
    /// One allowlisted target: the node path deliveries may be stored under and, when the
    /// integration signs its deliveries with a GitHub-style HMAC, the configuration key holding
    /// the shared secret (<see cref="SecretConfigKeyName"/>). A bare path IS a target — the
    /// implicit conversion keeps "allowlisted, unsigned" the plain, unceremonious case.
    /// </summary>
    public sealed record WebhookTarget(string Path, string? SecretConfigKey = null)
    {
        /// <summary>A bare path is an allowlisted target that declares no signature.</summary>
        public static implicit operator WebhookTarget(string path) => new(path);
    }

    /// <summary>
    /// The allowlist as configured: one <see cref="WebhookTarget"/> per <c>Targets:N</c> entry,
    /// carrying its <see cref="SecretConfigKeyName"/> child when it declares one. Entries whose
    /// value is not a plain node path are dropped (fail-closed). Pure over the configuration.
    /// </summary>
    public static ImmutableArray<WebhookTarget> ReadTargets(IConfiguration? configuration)
    {
        if (configuration is null)
            return [];
        var targets = ImmutableArray.CreateBuilder<WebhookTarget>();
        foreach (var entry in configuration.GetSection(TargetsConfigSection).GetChildren())
        {
            var path = NormalizeTarget(entry.Value);
            if (path is null)
                continue;
            var secretConfigKey = entry[SecretConfigKeyName];
            targets.Add(new WebhookTarget(
                path,
                string.IsNullOrWhiteSpace(secretConfigKey) ? null : secretConfigKey.Trim()));
        }
        return targets.ToImmutable();
    }

    /// <summary>
    /// GitHub-style HMAC verification: <c>sha256=&lt;hex&gt;</c> over the RAW body, compared in
    /// constant time. False for an absent, malformed or mismatched signature — every refusal shape
    /// is one verdict, because distinguishing them for the caller is what leaks the secret's shape.
    /// Pure.
    /// </summary>
    public static bool VerifyHmacSha256(string? signatureHeader, string body, string secret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)
            || !signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
        var provided = signatureHeader["sha256=".Length..];
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(provided.ToLowerInvariant()));
    }

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
    /// anchor under a real owner; an ownerless satellite NotFound-storms the router), and when the
    /// matched target declares a <see cref="SecretConfigKeyName"/> its
    /// <see cref="SignatureHeader"/> must verify over the raw body. The event is stored under the
    /// System identity (the anonymous caller has no write access anywhere — the allowlist is the
    /// authorization). Cold; never throws for a refused delivery — refusal is data.
    ///
    /// <para>🚨 The ORDER of the refusals is contract. Target first, so an unlisted path answers
    /// 404 without revealing whether it is signed; SIZE next, so an oversized body is refused
    /// before it is hashed; signature last, and always BEFORE the node is created — a delivery
    /// that fails to verify must leave nothing behind, or the endpoint has merely moved the silent
    /// drop from the consumer into the store (#3312).</para>
    /// </summary>
    public static IObservable<DeliveryResult> Deliver(
        IMessageHub hub,
        IReadOnlyCollection<WebhookTarget> allowedTargets,
        string? target,
        string? contentType,
        IEnumerable<KeyValuePair<string, string>> headers,
        string body)
    {
        var normalized = NormalizeTarget(target);
        if (normalized is null)
            return Observable.Return(new DeliveryResult(DeliveryStatus.UnknownTarget));
        var matched = allowedTargets.FirstOrDefault(t => string.Equals(
            NormalizeTarget(t.Path), normalized, StringComparison.Ordinal));
        if (matched is null)
            return Observable.Return(new DeliveryResult(DeliveryStatus.UnknownTarget));
        if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
            return Observable.Return(new DeliveryResult(DeliveryStatus.TooLarge));

        var kept = headers
            .Where(h => !DropHeader(h.Key))
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(
                g => g.Key,
                g => string.Join(", ", g.Select(h => h.Value)),
                StringComparer.OrdinalIgnoreCase);

        var signatureVerified = false;
        if (matched.SecretConfigKey is { Length: > 0 } secretConfigKey)
        {
            // Resolved per delivery, not captured once: the secret is rotatable configuration, and
            // a value read at startup would keep verifying against the retired one.
            var secret = hub.ServiceProvider.GetService<IConfiguration>()?[secretConfigKey];
            if (string.IsNullOrWhiteSpace(secret))
                return Observable.Return(new DeliveryResult(DeliveryStatus.SecretUnavailable));
            kept.TryGetValue(SignatureHeader, out var provided);
            if (!VerifyHmacSha256(provided, body, secret))
                return Observable.Return(new DeliveryResult(DeliveryStatus.SignatureInvalid));
            signatureVerified = true;
        }

        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Observable.Throw<DeliveryResult>(
                new InvalidOperationException("The mesh service is not available."));
        var accessService = hub.ServiceProvider.GetService<AccessService>();

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
                            .Select(_ => new DeliveryResult(DeliveryStatus.Accepted, node.Path)
                            {
                                SignatureVerified = signatureVerified,
                            });
                    }));
    }
}
