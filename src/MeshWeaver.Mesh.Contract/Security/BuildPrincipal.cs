using System.Collections.Immutable;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// A repository the mesh trusts to act as a BUILD — the rule GitHub's OIDC token is checked against
/// (#2483), and the whole reason there is no secret to keep.
///
/// <para><b>What this replaces.</b> Fetching an upstream's sealed publication used to need an Azure
/// OIDC identity whose federated credentials live in the Entra tenant: four of them, every one scoped
/// to <c>ref:refs/heads/main</c>, none for <c>pull_request</c> — so a gate could not fetch on the one
/// event it exists for (<c>AADSTS700213</c>, measured 2026-08-27). Nothing in the mesh recorded that
/// those credentials existed, which repos held one, or who authorised them. That is the shape of the
/// plaintext-provider-key incident: a security fact with no record a reader can point at. Here the
/// rule IS a node — <c>search nodeType:BuildPrincipal</c> is the complete list of repositories this
/// mesh trusts and exactly what each may do.</para>
///
/// <para>🚨 <b>It lives in the Admin partition</b> (<see cref="Namespace"/>), exactly like
/// <see cref="PluginGrant"/> and for exactly the same reason: the subject of an access decision must
/// not be able to write the decision. Only a global admin can write there
/// (<c>Doc/Architecture/AccessControl</c> → "The Admin partition"), which IS the gating — not a
/// second role check beside it that could drift.</para>
///
/// <para>🚨 <b>The path is a routing hint; the record is the authority.</b> A principal is looked up
/// at <see cref="BuildPrincipal.NodeId"/> of the token's <c>repository</c> claim, and then
/// <see cref="Repository"/> on the node it found is compared with the claim again
/// (<see cref="GitHubActionsToken.RepositoryEquals"/>). The match is EXACT — never a prefix, never a
/// wildcard. Same discipline as <see cref="MeshWeaverInstanceIndex"/>, and for the same reason: an
/// index that has drifted must not authenticate anybody.</para>
///
/// <para><b>Revocation is immediate, not deferred.</b> Setting <see cref="RequestedAction"/> to
/// <see cref="BuildPrincipalActions.Revoke"/> stops this principal authenticating on the very next
/// request, on every replica, before any watcher has folded it into <see cref="IsRevoked"/>. A
/// security stop that waits for a reactor is a security stop with a window.</para>
/// </summary>
public record BuildPrincipal
{
    /// <summary>The <c>_BuildPrincipal</c> namespace under the <b>Admin</b> partition. Mirrors
    /// <c>Admin/_PluginGrant</c>: an access decision, in the one partition its subject cannot
    /// write.</summary>
    public const string Namespace = "Admin/_BuildPrincipal";

    /// <summary>
    /// The repository this principal speaks for, as GitHub's <c>repository</c> claim names it —
    /// <c>Systemorph/MeshWeaver.SocialMedia</c>. Compared with
    /// <see cref="GitHubActionsToken.RepositoryEquals"/>, so an admin may write either the classic
    /// form or GitHub's immutable <c>owner@id/name@id</c> form and both keep matching.
    /// </summary>
    public string Repository { get; init; } = "";

    /// <summary>
    /// Optional pin on GitHub's immutable numeric repository id (<c>repository_id</c>). When set it
    /// must match exactly. Names can be renamed and re-registered; an id cannot — so pinning it is
    /// the strongest form of this rule, and leaving it null is the ordinary one.
    /// </summary>
    public string? RepositoryId { get; init; }

    /// <summary>Optional pin on the owner's immutable numeric id (<c>repository_owner_id</c>). Same
    /// contract as <see cref="RepositoryId"/>.</summary>
    public string? RepositoryOwnerId { get; init; }

    /// <summary>
    /// Which <c>event_name</c>s may act, and with which verbs — <c>{ "push": ["publish", "fetch"],
    /// "pull_request": ["fetch"] }</c>. An event that is not a key here may do nothing at all, so
    /// this is also the list of events this repository is trusted on. Verbs are
    /// <see cref="BuildVerbs"/> values, matched case-insensitively.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> Events { get; init; } =
        ImmutableDictionary<string, IReadOnlyCollection<string>>.Empty;

    /// <summary>
    /// Optional per-event pin on the run's <c>ref</c> claim — <c>{ "push": ["refs/heads/main"] }</c>
    /// — so "<c>push</c> on <c>main</c> may publish" is expressible rather than merely intended. An
    /// event with no entry here (or an empty list) is NOT ref-constrained, which is the right default
    /// for <c>pull_request</c>: its ref is <c>refs/pull/&lt;n&gt;/merge</c> and cannot be enumerated
    /// in advance. Matched ordinally — a git ref is a wire identifier, not operator prose.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> EventRefs { get; init; } =
        ImmutableDictionary<string, IReadOnlyCollection<string>>.Empty;

    /// <summary>
    /// What this repository may do, as <c>verb:source</c> — <c>publish:socialmedia</c>,
    /// <c>fetch:plugins</c>. This is the security tie: the identity that publishes a source is the
    /// identity that may fetch what it depends on, and it can do neither outside this list. Matched
    /// EXACTLY on both halves — no wildcard, no prefix. A <c>fetch:*</c> would be an all-sources
    /// grant written to look like a scope.
    /// </summary>
    public IReadOnlyCollection<string> Scopes { get; init; } = [];

    /// <summary>ObjectId of the global admin who created this principal. Part of the record because
    /// "who authorised this" is exactly what the Entra credentials could not answer.</summary>
    public string IssuedBy { get; init; } = "";

    /// <summary>When it was created.</summary>
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>Optional end of term. Null = until revoked. A build principal for a one-off
    /// migration should carry one.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Last time a run authenticated against this principal. Advisory — never consulted by a
    /// decision.
    ///
    /// <para>🚨 <b>Nothing writes it yet, deliberately.</b> Stamping it on the authentication path
    /// is a write per request, and the instance side only does it under a single-flight freshness
    /// discipline (<c>ApiToken.LastUsedAt</c>) that this leg has not needed yet. The field is here
    /// because the design names it and an admin may set it by hand; read an ABSENT value as "not
    /// recorded", never as "never used".</para>
    /// </summary>
    public DateTimeOffset? LastSeen { get; init; }

    /// <summary>
    /// The control-plane verb, in the shape every admin action here already takes
    /// (<c>Store/Provision</c>, <c>Store/Enrollment</c>): write
    /// <see cref="BuildPrincipalActions.Revoke"/> and this principal stops authenticating
    /// immediately. Never a request message — the field IS the request
    /// (<c>Doc/Architecture/RequestViaStreamUpdate</c>).
    /// </summary>
    public string? RequestedAction { get; init; }

    /// <summary>
    /// The permanent form of a revocation, for an admin who wants the record to read as revoked
    /// rather than as "asked to be". <see cref="RequestedAction"/> and this field are EQUIVALENT to
    /// the decision — <see cref="IsActive"/> refuses on either — so nothing has to run in between
    /// for a revoke to take effect.
    /// </summary>
    public bool IsRevoked { get; init; }

    /// <summary>When it was revoked.</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>ObjectId of the admin who revoked it.</summary>
    public string? RevokedBy { get; init; }

    /// <summary>Why this principal exists, in the issuer's words — a ticket, a repo owner, the
    /// upstream it seeds. Free text and advisory.</summary>
    public string? Notes { get; init; }

    /// <summary>
    /// Whether this principal is live at <paramref name="now"/> — not revoked, not asked to be
    /// revoked, within its term.
    /// </summary>
    /// <param name="now">The instant to judge.</param>
    /// <returns>True when it may still authenticate anything.</returns>
    public bool IsActive(DateTimeOffset now) =>
        !IsRevoked
        && !string.Equals(RequestedAction, BuildPrincipalActions.Revoke, StringComparison.OrdinalIgnoreCase)
        && (ExpiresAt is null || ExpiresAt > now);

    /// <summary>
    /// Whether the run described by <paramref name="claims"/> may perform <paramref name="verb"/> on
    /// registry source <paramref name="source"/> at <paramref name="now"/>.
    ///
    /// <para>🚨 <paramref name="now"/> is an ARGUMENT, never the ambient clock: this is an
    /// authorization decision, so it has to be reproducible in a test at a chosen instant. Same rule
    /// as <see cref="PluginGrant.Allows(string,string,DateTimeOffset)"/>.</para>
    /// </summary>
    /// <param name="claims">The verified token claims.</param>
    /// <param name="verb">A <see cref="BuildVerbs"/> value.</param>
    /// <param name="source">The registry source name.</param>
    /// <param name="now">The instant to decide at.</param>
    /// <returns>True when every check passes.</returns>
    public bool Allows(GitHubBuildClaims? claims, string? verb, string? source, DateTimeOffset now) =>
        Refuse(claims, verb, source, now) is null;

    /// <summary>
    /// The reason this principal refuses the run, or null when it allows it — one implementation
    /// behind <see cref="Allows"/> so the log line and the decision cannot disagree.
    ///
    /// <para>🚨 The reason is for the LOG and the entitlement ledger, never for the HTTP body. A
    /// refusal that says which check failed is an oracle over a fully predictable URL space — the
    /// same reasoning that makes a refused bundle byte-identical to "no such bundle".</para>
    /// </summary>
    /// <param name="claims">The verified token claims.</param>
    /// <param name="verb">A <see cref="BuildVerbs"/> value.</param>
    /// <param name="source">The registry source name.</param>
    /// <param name="now">The instant to decide at.</param>
    /// <returns>The refusal reason, or null when allowed.</returns>
    public string? Refuse(GitHubBuildClaims? claims, string? verb, string? source, DateTimeOffset now)
    {
        if (claims is null)
            return "no verified claims";
        if (string.IsNullOrWhiteSpace(verb) || string.IsNullOrWhiteSpace(source))
            return "the request named no verb or no source";
        if (!IsActive(now))
            return IsRevoked
                || string.Equals(RequestedAction, BuildPrincipalActions.Revoke, StringComparison.OrdinalIgnoreCase)
                ? "the build principal is revoked"
                : "the build principal's term has ended";

        // The path routed us here; the RECORD decides. A drifted or hand-edited index must not
        // authenticate a repository the node does not name.
        if (!GitHubActionsToken.RepositoryEquals(Repository, claims.Repository))
            return $"the node declares repository '{Repository}' but the token claims '{claims.Repository}'";

        // Optional immutable pins. Absent means "not pinned"; present means EXACT.
        if (!string.IsNullOrWhiteSpace(RepositoryId)
            && !string.Equals(RepositoryId.Trim(), claims.RepositoryId, StringComparison.Ordinal))
            return "the pinned repository id does not match the token";
        if (!string.IsNullOrWhiteSpace(RepositoryOwnerId)
            && !string.Equals(RepositoryOwnerId.Trim(), claims.RepositoryOwnerId, StringComparison.Ordinal))
            return "the pinned repository owner id does not match the token";

        if (string.IsNullOrWhiteSpace(claims.EventName))
            return "the token carries no event_name";
        var verbs = Lookup(Events, claims.EventName);
        if (verbs is null)
            return $"event '{claims.EventName}' is not listed on this principal";
        if (!verbs.Any(v => string.Equals(v, verb, StringComparison.OrdinalIgnoreCase)))
            return $"event '{claims.EventName}' may not '{verb}'";

        // A ref pin applies only to the events that declare one — see EventRefs.
        var refs = Lookup(EventRefs, claims.EventName);
        if (refs is { Count: > 0 }
            && !refs.Any(r => string.Equals(r, claims.Ref, StringComparison.Ordinal)))
            return $"ref '{claims.Ref}' is not permitted for event '{claims.EventName}'";

        var wanted = Scope(verb, source);
        return Scopes.Any(s => string.Equals(NormalizeScope(s), wanted, StringComparison.Ordinal))
            ? null
            : $"the principal holds no '{wanted}' scope";
    }

    /// <summary>The canonical <c>verb:source</c> scope string for a request.</summary>
    /// <param name="verb">A <see cref="BuildVerbs"/> value.</param>
    /// <param name="source">The registry source name.</param>
    /// <returns>The normalized scope string.</returns>
    public static string Scope(string? verb, string? source) =>
        $"{(verb ?? "").Trim().ToLowerInvariant()}:{(source ?? "").Trim().ToLowerInvariant()}";

    /// <summary>
    /// The node id a repository resolves to — lowercase, <c>/</c> folded to <c>--</c>, and every
    /// character outside <c>[a-z0-9._-]</c> replaced, so the id is deterministic and path-safe.
    /// GitHub owner and repository names are case-insensitive, so lowercasing cannot collide two
    /// distinct repositories.
    /// </summary>
    /// <param name="repository">A <c>repository</c> claim or a declared repository.</param>
    /// <returns>The node id, or an empty string when the repository is blank.</returns>
    public static string NodeId(string? repository)
    {
        var normalized = GitHubActionsToken.NormalizeRepository(repository);
        if (normalized.Length == 0)
            return "";
        var builder = new System.Text.StringBuilder(normalized.Length + 1);
        foreach (var c in normalized.ToLowerInvariant())
            builder.Append(c switch
            {
                '/' => "--",
                >= 'a' and <= 'z' => c.ToString(),
                >= '0' and <= '9' => c.ToString(),
                '.' or '_' or '-' => c.ToString(),
                _ => "-",
            });
        return builder.ToString();
    }

    /// <summary>The full node path a repository's principal is read from.</summary>
    /// <param name="repository">A <c>repository</c> claim or a declared repository.</param>
    /// <returns>The path, or an empty string when the repository is blank.</returns>
    public static string PathFor(string? repository)
    {
        var id = NodeId(repository);
        return id.Length == 0 ? "" : $"{Namespace}/{id}";
    }

    private static string NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return "";
        var colon = scope.IndexOf(':');
        return colon <= 0 || colon == scope.Length - 1
            ? ""
            : Scope(scope[..colon], scope[(colon + 1)..]);
    }

    /// <summary>Case-insensitive lookup — an event name is a GitHub wire identifier, but a
    /// hand-authored node routinely carries <c>Push</c> where the claim says <c>push</c>, and a
    /// principal that silently matches nothing is the worst possible failure mode for an admin.</summary>
    private static IReadOnlyCollection<string>? Lookup(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? map, string key)
    {
        if (map is null or { Count: 0 })
            return null;
        if (map.TryGetValue(key, out var exact))
            return exact;
        foreach (var pair in map)
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        return null;
    }
}

/// <summary>The verbs a <see cref="BuildPrincipal"/> scope can carry.</summary>
public static class BuildVerbs
{
    /// <summary>Read a source's sealed publication from this registry — what an
    /// <c>upstream-seed</c> gate needs.</summary>
    public const string Fetch = "fetch";

    /// <summary>Write a source's bytes INTO this registry.</summary>
    public const string Publish = "publish";
}

/// <summary>The control-plane verbs <see cref="BuildPrincipal.RequestedAction"/> accepts.</summary>
public static class BuildPrincipalActions
{
    /// <summary>End this principal. Honoured the moment it is written — <see cref="BuildPrincipal.IsActive"/>
    /// reads this field itself, so nothing has to observe the node and fold it into
    /// <see cref="BuildPrincipal.IsRevoked"/> first. A security stop that waits for a reactor is a
    /// security stop with a window.</summary>
    public const string Revoke = "Revoke";
}
