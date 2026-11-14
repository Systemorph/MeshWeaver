using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using MeshWeaver.Data;
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

    /// <summary>
    /// The value of the header <paramref name="name"/>, or null when the delivery carried none —
    /// matched IGNORING CASE, which is how a consumer must read a stored delivery (#4066).
    ///
    /// <para>🚨 Never <c>Headers.TryGetValue(name, …)</c> on a stored event. Header names are
    /// case-insensitive on the wire and HTTP/2 delivers them lowercased
    /// (<c>x-hub-signature-256</c>); the endpoint verifies through a case-insensitive dictionary, but
    /// that comparer does not survive the store — the event a consumer reads back is re-typed from
    /// JSON with the default, case-sensitive one. A canonical-case lookup then misses the header the
    /// endpoint just verified, and the consumer refuses a delivery the sender was told is accepted.</para>
    /// </summary>
    /// <param name="name">The header name, in any case.</param>
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
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

    /// <summary>
    /// The child of a target entry DECLARING which GitHub event kinds that target accepts —
    /// <c>WebhookInbox:Targets:0:AcceptedEvents = pull_request,issues</c> (env:
    /// <c>WebhookInbox__Targets__0__AcceptedEvents</c>), or a list under it
    /// (<c>…:AcceptedEvents:0</c>, <c>…:AcceptedEvents:1</c>). Matched against the delivery's
    /// <see cref="GitHubEventHeader"/>, ignoring case (MeshWeaver#7863).
    ///
    /// <para>🚨 Declared on the allowlist entry, like <see cref="SecretConfigKeyName"/>, for the same
    /// reason: the record that makes a target reachable says what it takes in, so the two cannot
    /// drift. An organisation webhook subscribes to whole event KINDS for every target it feeds, and
    /// a target that consumes three of them otherwise stores, indexes and deletes the rest.</para>
    ///
    /// <para>A target that declares NOTHING (absent or blank) accepts every kind — the contract every
    /// target had before this key existed. A delivery that carries no <see cref="GitHubEventHeader"/>
    /// is not a GitHub delivery (the fleet's own signed envelopes, a Stripe event) and is never
    /// filtered by it. A delivery of an undeclared kind is answered 2xx with
    /// <see cref="DeliveryResult.IgnoredEvent"/> set, counted, and NEVER written: the sender did
    /// nothing wrong, so it must not see a refusal and retry.</para>
    /// </summary>
    public const string AcceptedEventsName = "AcceptedEvents";

    /// <summary>The header naming a GitHub webhook delivery's event kind, matched by
    /// <see cref="AcceptedEventsName"/>.</summary>
    public const string GitHubEventHeader = "X-GitHub-Event";

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

        /// <summary>
        /// The SENDER key that verified the delivery — the child name under the target's
        /// <see cref="WebhookTarget.SecretConfigKey"/> (<see cref="SenderKeyOf"/>) — or null when
        /// the target's own shared secret did, or nothing was verified. The inbox only STORES on
        /// it; what a sender key is allowed to cause is the CONSUMER's decision, made again over
        /// the stored body (MeshWeaver.Plugins#1913).
        /// </summary>
        public string? SenderKey { get; init; }

        /// <summary>
        /// True when the delivery asked only to be VERIFIED (<see cref="VerifyOnlyHeader"/>): it
        /// verified, and nothing was stored (<see cref="NodePath"/> is null).
        /// </summary>
        public bool VerifyOnly { get; init; }

        /// <summary>
        /// The <see cref="GitHubEventHeader"/> kind of a delivery the target does not accept
        /// (<see cref="AcceptedEventsName"/>): it was answered, counted and NOT stored
        /// (<see cref="NodePath"/> is null). Null for every delivery that was stored, or refused.
        /// </summary>
        public string? IgnoredEvent { get; init; }
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

        /// <summary>
        /// The GitHub event kinds this target accepts (<see cref="AcceptedEventsName"/>), compared
        /// ignoring case; null when it declares none, which accepts every kind. An init-only
        /// property rather than a positional parameter, so modules compiled against the two-argument
        /// constructor keep binding.
        /// </summary>
        /// <remarks>Normalized on init: whatever comparer the assigned set carries, the target
        /// compares ignoring case, and an empty set is no declaration (accept all) — the same
        /// reading <see cref="ReadTargets"/> gives a blank configuration value.</remarks>
        public ImmutableHashSet<string>? AcceptedEvents
        {
            get => acceptedEvents;
            init => acceptedEvents = value is null || value.IsEmpty
                ? null
                : value.WithComparer(StringComparer.OrdinalIgnoreCase);
        }

        private readonly ImmutableHashSet<string>? acceptedEvents;

        /// <summary>
        /// Whether a delivery carrying <paramref name="gitHubEvent"/> is one this target takes in:
        /// always when it declares no <see cref="AcceptedEvents"/>, always when the delivery names no
        /// event (it is not a GitHub delivery), else only a declared kind. Pure.
        /// </summary>
        /// <param name="gitHubEvent">The delivery's <see cref="GitHubEventHeader"/>, or null.</param>
        public bool Accepts(string? gitHubEvent) =>
            AcceptedEvents is null
            || string.IsNullOrWhiteSpace(gitHubEvent)
            || AcceptedEvents.Contains(gitHubEvent.Trim());
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
                string.IsNullOrWhiteSpace(secretConfigKey) ? null : secretConfigKey.Trim())
            {
                AcceptedEvents = ReadAcceptedEvents(entry.GetSection(AcceptedEventsName)),
            });
        }
        return targets.ToImmutable();
    }

    /// <summary>
    /// A target's <see cref="AcceptedEventsName"/> declaration: the section's own value split on
    /// commas (the one-key env form) plus every child value (the list form). Null when nothing is
    /// declared — a blank value is no declaration, so a chart that renders the key empty keeps the
    /// accept-all contract instead of silently refusing every kind. Pure over the configuration.
    /// </summary>
    private static ImmutableHashSet<string>? ReadAcceptedEvents(IConfigurationSection section)
    {
        var declared = (section.Value ?? "").Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Concat(section.GetChildren().Select(c => c.Value).OfType<string>())
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        return declared.IsEmpty ? null : declared;
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

    /// <summary>
    /// The per-SENDER key a delivery verifies against when the target's own shared secret does not:
    /// the name of the first child of <paramref name="secretConfigKey"/>'s configuration section
    /// whose value verifies <paramref name="signatureHeader"/> over <paramref name="body"/> — e.g.
    /// <c>Hosting:PlatformWebhookSecret:fabrikam</c> (env
    /// <c>Hosting__PlatformWebhookSecret__fabrikam</c>) answers <c>fabrikam</c>. Null when none does.
    ///
    /// <para>🚨 The same shape <c>Hosting:ModuleReportSecret:{deployment}</c> already has, and for
    /// the same reason: a secret held by every sender proves possession, not identity
    /// (MeshWeaver.Plugins#1913). A sender with its OWN key can be told apart from every other,
    /// so the consumer can decide what that one sender may cause. This inbox decides only whether
    /// the bytes are worth storing — it never widens what a delivery is allowed to do.</para>
    ///
    /// <para>🚨 The answer is an IDENTITY, so it must be unambiguous: when MORE than one child
    /// verifies (two senders provisioned with the same value), no sender can be named and the
    /// delivery is refused rather than attributed to whichever child the configuration happens to
    /// list first. A blank child is no key. Pure over the configuration.</para>
    /// </summary>
    public static string? SenderKeyOf(
        IConfiguration? configuration, string secretConfigKey, string? signatureHeader, string body) =>
        MatchSender(InstanceSecrets.Candidates(null, configuration, secretConfigKey), signatureHeader, body)?.Sender;

    /// <summary>
    /// Which per-sender key verified a delivery, and whether it was that sender's CURRENT key or
    /// the previous one a rotation still accepts (<see cref="InstanceSecrets.PreviousSubKey"/>).
    /// </summary>
    /// <param name="Sender">The child name, e.g. a deployment id.</param>
    /// <param name="WithCurrent">True when the current key verified; false for the rotation's previous key.</param>
    public sealed record SenderMatch(string Sender, bool WithCurrent);

    /// <summary>
    /// The sender whose key verifies <paramref name="signatureHeader"/> over <paramref name="body"/>,
    /// among <paramref name="candidates"/> (<see cref="InstanceSecrets.Candidates"/>: per child, the
    /// current key first, then a rotation's previous key). Null when none does, and null when MORE
    /// than one sender does, because then nobody can be named (see <see cref="SenderKeyOf"/>).
    /// Pure.
    /// </summary>
    public static SenderMatch? MatchSender(
        IEnumerable<(string Child, ImmutableList<string> Values)> candidates, string? signatureHeader, string body)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
            return null;
        SenderMatch? match = null;
        foreach (var (child, values) in candidates)
        {
            var index = values.FindIndex(v => !string.IsNullOrWhiteSpace(v) && VerifyHmacSha256(signatureHeader, body, v));
            if (index < 0)
                continue;
            if (match is not null)
                return null;                    // ambiguous — two senders share one key
            match = new SenderMatch(child, index == 0);
        }
        return match;
    }

    /// <summary>
    /// The request header that asks the inbox to VERIFY a delivery without storing it
    /// (value <c>true</c>). A sender uses it to test its key: the answer says whether the signature
    /// verified and, for a per-sender key, as whom. Nothing is written, so a test causes nothing.
    /// </summary>
    public const string VerifyOnlyHeader = "X-MeshWeaver-Verify-Only";

    /// <summary>Whether the headers ask for a verify-only delivery. Pure.</summary>
    public static bool IsVerifyOnly(IEnumerable<KeyValuePair<string, string>> headers) =>
        headers.Any(h => string.Equals(h.Key, VerifyOnlyHeader, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(h.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Registers the WebhookEvent node type on the mesh builder, and the inbox's meter
    /// (<see cref="WebhookInboxMetrics"/>).</summary>
    public static TBuilder AddWebhookInbox<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.WithMeshType<WebhookEvent>();
        builder.ConfigureServices(services => services.AddSingleton<WebhookInboxMetrics>());
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
    ///
    /// <para>🚨 A target that declares <see cref="AcceptedEventsName"/> answers a delivery of any
    /// other GitHub event kind 2xx with <see cref="DeliveryResult.IgnoredEvent"/> set and writes
    /// NOTHING (MeshWeaver#7863). That filter runs after the signature, so it never turns a 401 into
    /// a 2xx.</para>
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

        var verifyOnly = IsVerifyOnly(headers);
        if (matched.SecretConfigKey is not { Length: > 0 } secretConfigKey)
            // A verify-only request to a target that verifies nothing has nothing to test.
            return verifyOnly
                ? Observable.Return(new DeliveryResult(DeliveryStatus.Accepted) { VerifyOnly = true })
                : IgnoredEventOf(matched, kept) is { } unaccepted
                    ? Store(hub, normalized, contentType, kept, body, signatureVerified: false, senderKey: null, ignoredEvent: unaccepted)
                    : Store(hub, normalized, contentType, kept, body, signatureVerified: false, senderKey: null);

        // The portal-set sender keys (InstanceSecrets) are read from a live catalog; wait for its
        // first listing so the first delivery after a start is not checked against configuration alone.
        var catalog = hub.ServiceProvider.GetService<InstanceSecretCatalog>();
        var ready = catalog?.WhenLoaded ?? Observable.Return(System.Reactive.Unit.Default);
        return ready.SelectMany(_ =>
        {
            // Resolved per delivery, not captured once: the secret is rotatable configuration, and
            // a value read at startup would keep verifying against the retired one.
            var configuration = hub.ServiceProvider.GetService<IConfiguration>();
            var secret = configuration?[secretConfigKey];
            if (string.IsNullOrWhiteSpace(secret))
                return Observable.Return(new DeliveryResult(DeliveryStatus.SecretUnavailable));
            kept.TryGetValue(SignatureHeader, out var provided);
            SenderMatch? sender = null;
            if (!VerifyHmacSha256(provided, body, secret))
            {
                // Not the shared secret — a sender holding its OWN key under this one (Plugins#1913)?
                // Its key may be mounted (configuration) or set in the portal (InstanceSecrets), and
                // during a rotation its previous key still verifies.
                sender = MatchSender(InstanceSecrets.Candidates(catalog, configuration, secretConfigKey), provided, body);
                if (sender is null)
                    return Observable.Return(new DeliveryResult(DeliveryStatus.SignatureInvalid));
                // Recorded on the portal-set key (a no-op for a mounted one): when it was last
                // verified, and — the first time the CURRENT key verifies — the end of its rotation.
                InstanceSecrets.RecordUse(hub, $"{secretConfigKey}:{sender.Sender}", ok: true,
                        result: verifyOnly
                            ? LocalizableText.Keyed($"test signature verified as '{sender.Sender}'",
                                "secret.use.testVerified", ("sender", sender.Sender))
                            : LocalizableText.Keyed($"announcement verified as '{sender.Sender}'",
                                "secret.use.announcementVerified", ("sender", sender.Sender)),
                        verifiedWithCurrent: sender.WithCurrent)
                    .Subscribe(_ => { }, _ => { });
            }
            if (verifyOnly)
                return Observable.Return(new DeliveryResult(DeliveryStatus.Accepted)
                {
                    SignatureVerified = true,
                    SenderKey = sender?.Sender,
                    VerifyOnly = true,
                });
            // Filtered only AFTER the signature verified: an undeclared kind is still answered 401
            // when its signature does not verify, so a drifted secret stays visible on every kind.
            return Store(hub, normalized, contentType, kept, body, signatureVerified: true, senderKey: sender?.Sender,
                ignoredEvent: IgnoredEventOf(matched, kept));
        });
    }

    /// <summary>The delivery's <see cref="GitHubEventHeader"/> when <paramref name="target"/> does
    /// NOT accept it (<see cref="WebhookTarget.Accepts"/>); null when it is to be stored. Pure.</summary>
    private static string? IgnoredEventOf(WebhookTarget target, ImmutableDictionary<string, string> headers)
    {
        headers.TryGetValue(GitHubEventHeader, out var gitHubEvent);
        return target.Accepts(gitHubEvent) ? null : gitHubEvent!.Trim();
    }

    /// <summary>
    /// Stores the delivery under its owner — or, when <paramref name="ignoredEvent"/> is set (a kind
    /// the target does not accept), answers it WITHOUT writing and counts it on
    /// <see cref="WebhookInboxMetrics"/>. Either way the owner node is checked FIRST, so an
    /// allowlisted path with no owner is <see cref="DeliveryStatus.UnknownTarget"/> for every kind:
    /// an ignored kind never answers 2xx where a stored one would be refused.
    /// </summary>
    private static IObservable<DeliveryResult> Store(
        IMessageHub hub, string normalized, string? contentType,
        ImmutableDictionary<string, string> kept, string body, bool signatureVerified, string? senderKey,
        string? ignoredEvent = null)
    {
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null)
            return Observable.Throw<DeliveryResult>(
                new InvalidOperationException("The mesh service is not available."));
        var accessService = hub.ServiceProvider.GetService<AccessService>();

        // 🚨 RunAsSystem, never `Observable.Using(access.ImpersonateAsSystem, …)` (#1790): Using opens
        // the AsyncLocal scope on the SUBSCRIBING thread and disposes it wherever the create's
        // response lands, so the subscriber stayed latched as System. Tolerable on an HTTP request
        // thread that ends with the request; not on an in-process caller — the self-update poller's
        // pool thread delivering into this instance's own inbox (SelfUpdateHandover, #4098) — which
        // would carry System into everything it did next. The seal enters and leaves inside one
        // synchronous Subscribe and delivers every notification under the subscriber's own identity.
        return accessService.RunAsSystem(
                () => mesh
                    .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{normalized}")).Take(1)
                    .Select(c => c.Items.FirstOrDefault(n => n.Path == normalized))
                    .SelectMany(owner =>
                    {
                        if (owner is null)
                            return Observable.Return(new DeliveryResult(DeliveryStatus.UnknownTarget));
                        if (ignoredEvent is not null)
                        {
                            // Optional by design: a mesh that never called AddWebhookInbox still
                            // answers; only the count is missing, and the answer names the kind.
                            hub.ServiceProvider.GetService<WebhookInboxMetrics>()?.Ignored(normalized);
                            return Observable.Return(new DeliveryResult(DeliveryStatus.Accepted)
                            {
                                SignatureVerified = signatureVerified,
                                SenderKey = senderKey,
                                IgnoredEvent = ignoredEvent,
                            });
                        }
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
                        // 🚨 Answered on the COMMIT, not on the create's reply (#6039). The sender
                        // needs to know the delivery is durable. The reply additionally waits for
                        // the post-creation handlers and for its turn on the node-operation hub's
                        // block, and GitHub recorded such a wait as "no response in 10 s" for a
                        // delivery committed in 200 ms. No handler is part of a WebhookEvent's
                        // contract: consumers pick events up from their own inbox query.
                        return mesh.CreateNodeAnsweredOnCommit(hub.ServiceProvider, node)
                            // The consumer drains its inbox from its OWN hub, which only runs while that
                            // node is activated — and a committed satellite does not activate its owner. So
                            // the delivery WAKES the owner; without it a verified delivery waited until
                            // somebody opened the node (MeshWeaver.Plugins#5435: 140 runs queued overnight).
                            .Do(_ => WakeOwner(hub, normalized))
                            .Select(_ => new DeliveryResult(DeliveryStatus.Accepted, node.Path)
                            {
                                SignatureVerified = signatureVerified,
                                SenderKey = senderKey,
                            });
                    }));
    }

    /// <summary>
    /// Wakes the inbox's OWNER after a delivery is stored: a <see cref="PingRequest"/> to its address
    /// activates the owner's hub — where the consumer's inbox watch is armed — so the delivery is
    /// processed now rather than at the owner's next activation. Fire-and-forget and bounded: the
    /// answer to the sender never waits on it, and a wake that fails changes nothing (the delivery is
    /// stored and is drained at the owner's next activation either way).
    /// </summary>
    internal static void WakeOwner(IMessageHub hub, string ownerPath)
    {
        try
        {
            hub.NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(ownerPath)))
                .Take(1)
                .Timeout(OwnerWakeBudget)
                .Subscribe(_ => { }, _ => { });
        }
        catch (Exception)
        {
            // a wake is a courtesy, never a precondition of the delivery
        }
    }

    /// <summary>How long a wake of the inbox's owner may take before it is abandoned.</summary>
    internal static readonly TimeSpan OwnerWakeBudget = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The webhook inbox's counters, on the platform meter (<see cref="PlatformMetrics.MeterName"/>, which
/// the portal already exports). One counter today: <c>meshweaver.webhook.deliveries.ignored</c>, a
/// delivery answered and NOT written because its target does not accept its GitHub event kind
/// (<see cref="WebhookInbox.AcceptedEventsName"/>, MeshWeaver#7863). Counted rather than logged at
/// Information: at the organisation webhook's rate that would be thousands of lines an hour that
/// each say the same thing.
///
/// <para>🚨 Tagged by TARGET only. A target is an allowlisted path, so the tag is bounded by
/// configuration; the event kind is not tagged, because on a target that declares no secret it is
/// whatever an anonymous caller writes into a header. The endpoint logs the kind at Debug.</para>
///
/// <para>An INSTANCE owned by the mesh (registered by <see cref="WebhookInbox.AddWebhookInbox{TBuilder}"/>),
/// never a static: its meter is disposed with the mesh, so two meshes in one process never count
/// into one series (Doc/Architecture/NoStaticState).</para>
/// </summary>
public sealed class WebhookInboxMetrics : IDisposable
{
    /// <summary>The counter's instrument name.</summary>
    public const string IgnoredCounterName = "meshweaver.webhook.deliveries.ignored";

    private readonly Meter meter;
    private readonly Counter<long> ignored;

    /// <summary>Creates the mesh's inbox meter.</summary>
    public WebhookInboxMetrics()
    {
        meter = new Meter(PlatformMetrics.MeterName);
        ignored = meter.CreateCounter<long>(
            IgnoredCounterName,
            unit: "{delivery}",
            description: "Webhook deliveries answered and not stored because their target does not accept "
                         + "their GitHub event kind (WebhookInbox:Targets:N:AcceptedEvents).");
    }

    /// <summary>Counts one ignored delivery to <paramref name="target"/>.</summary>
    /// <param name="target">The allowlisted target path.</param>
    public void Ignored(string target) =>
        ignored.Add(1, new KeyValuePair<string, object?>("target", target));

    /// <summary>Releases the meter with the mesh that owns it.</summary>
    public void Dispose() => meter.Dispose();
}
