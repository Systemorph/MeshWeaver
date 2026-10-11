using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.ActionLinks;

/// <summary>
/// A registered site an action is BOUND to: its identity in the site registry and its one canonical
/// origin.
///
/// <para>🚨 <b>The origin comes from configuration, never from the request.</b> A caller composes
/// this from the site registration. It is never built from a <c>Host</c> header, a forwarded
/// header, a query string or anything else a visitor controls — those are the inputs an open
/// redirect is made of.</para>
/// </summary>
public sealed record ActionSite
{
    /// <summary>The site's identity in the registry it was resolved from.</summary>
    public required string SiteId { get; init; }

    /// <summary>
    /// The site's canonical origin: scheme, host and (only when not the scheme's default) port —
    /// <c>https://portal.example.com</c>. No path, no trailing slash. See <see cref="NormalizeOrigin"/>.
    /// </summary>
    public required string Origin { get; init; }

    /// <summary>Whether <see cref="Origin"/> is already in the canonical form <see cref="NormalizeOrigin"/> produces.</summary>
    [JsonIgnore]
    public bool IsCanonical => NormalizeOrigin(Origin) is { } canonical
                               && string.Equals(canonical, Origin, StringComparison.Ordinal);

    /// <summary>
    /// Whether an action bound to THIS site may be served by <paramref name="serving"/> — the same
    /// site identity AND the same canonical origin. Ordinal on both, so a look-alike host or a
    /// second registration reusing an origin does not match. Pure.
    /// </summary>
    /// <param name="serving">The site serving the request, from this portal's own registration.</param>
    /// <returns>True when both the identity and the origin match.</returns>
    public bool IsBoundTo(ActionSite? serving) =>
        serving is not null
        && string.Equals(SiteId, serving.SiteId, StringComparison.Ordinal)
        && NormalizeOrigin(Origin) is { } mine
        && NormalizeOrigin(serving.Origin) is { } theirs
        && string.Equals(mine, theirs, StringComparison.Ordinal);

    /// <summary>
    /// The canonical form of an origin, or null when <paramref name="value"/> is not one. Pure.
    ///
    /// <para>Accepted: an absolute <c>https</c> URL with a host and nothing after it but an optional
    /// <c>/</c> — and <c>http</c> for a loopback host only, which is what a developer portal runs
    /// on. Refused: user-info, a path, a query, a fragment, any other scheme. The result is
    /// lower-case, with the port dropped when it is the scheme's default.</para>
    /// </summary>
    /// <param name="value">The candidate origin.</param>
    /// <returns><c>scheme://host[:port]</c>, or null.</returns>
    public static string? NormalizeOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048)
            return null;
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\'))
            return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;
        var https = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var http = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!https && !(http && uri.IsLoopback))
            return null;
        if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        if (uri.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;
        var scheme = https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        var host = uri.IdnHost.ToLowerInvariant();
        if (uri.HostNameType == UriHostNameType.IPv6 && !host.StartsWith('['))
            host = $"[{host}]";
        return uri.IsDefaultPort ? $"{scheme}://{host}" : $"{scheme}://{host}:{uri.Port}";
    }
}

/// <summary>
/// The facts a handler is given when an invocation is PUBLISHED: who publishes, to which site, and
/// the publisher's own request id that makes the publication idempotent.
///
/// <para>Every value is resolved by the server. <see cref="Publisher"/> is the authenticated
/// principal of the publishing activity, never a field read from a payload.</para>
/// </summary>
public sealed record ActionPublicationContext
{
    /// <summary>The authenticated identity publishing the action.</summary>
    public required string Publisher { get; init; }

    /// <summary>The registered site the action is published to and will be bound to.</summary>
    public required ActionSite Site { get; init; }

    /// <summary>
    /// The publisher's own id for this publication. Publishing is idempotent for
    /// (<see cref="Publisher"/>, <see cref="PublisherRequestId"/>): repeating the request returns
    /// the existing link, never a second one.
    /// </summary>
    public required string PublisherRequestId { get; init; }

    /// <summary>The path of the activity the publication runs as, when it runs as one.</summary>
    public string? ActivityPath { get; init; }

    /// <summary>The deterministic key of this publication (<see cref="ActionIdempotency.Key"/>).</summary>
    [JsonIgnore]
    public string IdempotencyKey =>
        ActionIdempotency.Key("action-publication", Site.SiteId, Publisher, PublisherRequestId);
}

/// <summary>
/// The facts a handler is given when a RECIPIENT arrives at a published action: who they are, which
/// site is serving them, and which link and revision they followed.
///
/// <para>🚨 <b>The recipient is the authenticated principal, and only that.</b> The composer sets
/// <see cref="Recipient"/> from the request's access context after sign-in. It is never taken from
/// the link, the invocation payload, a prefill, a query string or a posted form — a person cannot
/// be made the buyer of something by a link somebody else built.</para>
/// </summary>
public sealed record ActionRecipientContext
{
    /// <summary>The authenticated identity of the person at the link.</summary>
    public required string Recipient { get; init; }

    /// <summary>The site serving this request, from the portal's own registration.</summary>
    public required ActionSite Site { get; init; }

    /// <summary>The published link's identifier. An address, never a credential.</summary>
    public required string LinkId { get; init; }

    /// <summary>
    /// The revision of the link's definition the recipient was shown. A handler binds what it does
    /// to this snapshot, not to whatever a mutable link means later.
    /// </summary>
    public int DefinitionRevision { get; init; } = 1;

    /// <summary>The viewer's locale, for the handler's own viewer-facing text. Never derived from the process culture.</summary>
    public string? Locale { get; init; }

    /// <summary>
    /// Whether a real, signed-in person is present: a non-blank identity that is neither the
    /// anonymous visitor, nor the "every signed-in user" grant subject, nor the system identity.
    /// </summary>
    [JsonIgnore]
    public bool IsAuthenticated =>
        !string.IsNullOrWhiteSpace(Recipient)
        && !string.Equals(Recipient, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Recipient, WellKnownUsers.Public, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Recipient, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this recipient may be shown an action bound to <paramref name="boundTo"/>, as a
    /// status word: <see cref="ActionOutcomeStatus.Unauthenticated"/> when nobody is signed in
    /// (checked FIRST, so an anonymous visitor learns nothing about the link, not even that it
    /// belongs to another site), <see cref="ActionOutcomeStatus.OriginMismatch"/> when the action is
    /// bound to a different site than the one serving the request, else
    /// <see cref="ActionOutcomeStatus.Ready"/>. Pure.
    /// </summary>
    /// <param name="boundTo">The site the published action is bound to.</param>
    /// <returns>The admission status word.</returns>
    public string Admission(ActionSite boundTo)
    {
        ArgumentNullException.ThrowIfNull(boundTo);
        if (!IsAuthenticated)
            return ActionOutcomeStatus.Unauthenticated;
        return boundTo.IsBoundTo(Site) ? ActionOutcomeStatus.Ready : ActionOutcomeStatus.OriginMismatch;
    }

    /// <summary>
    /// A deterministic key for this recipient's one piece of work under <paramref name="scope"/> on
    /// this site (<see cref="ActionIdempotency.Key"/>). The scope is the HANDLER's choice of what
    /// must be unique — a coupon offer rather than a link, so that printing a second link cannot
    /// reset an offer. The link id is deliberately not part of the key.
    /// </summary>
    /// <param name="scope">What must be unique per recipient, as the handler defines it.</param>
    /// <returns>The idempotency key.</returns>
    public string IdempotencyKey(string scope) =>
        ActionIdempotency.Key("action-recipient", Site.SiteId, Recipient, scope);
}

/// <summary>Deterministic idempotency keys for action publication and execution.</summary>
public static class ActionIdempotency
{
    /// <summary>
    /// A stable key over <paramref name="parts"/>: the lower-case hex SHA-256 of the parts, each
    /// prefixed with its UTF-8 byte length so that <c>("ab","c")</c> and <c>("a","bc")</c> cannot
    /// collide. The same parts give the same key on every process and after every restart, which is
    /// what lets a retry find the work an earlier attempt started. Pure.
    /// </summary>
    /// <param name="parts">The values the work is unique for; none may be null.</param>
    /// <returns>A 64-character key.</returns>
    public static string Key(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Length == 0)
            throw new ArgumentException("An idempotency key needs at least one part.", nameof(parts));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            if (part is null)
                throw new ArgumentException("An idempotency key part may not be null.", nameof(parts));
            var bytes = Encoding.UTF8.GetBytes(part);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
