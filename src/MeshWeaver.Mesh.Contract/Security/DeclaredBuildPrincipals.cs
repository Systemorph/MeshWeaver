using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The build principals a deployment DECLARES in its configuration — the second place a
/// <see cref="BuildPrincipal"/> can come from, beside the node a global admin writes under
/// <see cref="BuildPrincipal.Namespace"/> (policy <c>declared-not-derived</c>).
///
/// <para><b>Why a declaration exists at all.</b> An instance that must be reachable by a build (the
/// combo verification lane reaches every instance of an estate as the run's own identity) needs the
/// trust rule on EVERY instance. As a node, that rule was created by hand, per instance, by whoever
/// held a global admin's session there — and an instance nobody could reach that way simply never
/// got it. A deployment record already carries the audience this leg checks; the trust rule now
/// travels the same governed route, so an estate declares it once per record instead of creating it
/// once per portal.</para>
///
/// <para>🚨 <b>A declaration is STRICTER than a node, never looser.</b> A node may leave the id pins
/// out, may leave an event un-pinned to a ref, and may carry any scope. A declaration may not: it
/// must pin <see cref="BuildPrincipal.RepositoryId"/> and <see cref="BuildPrincipal.RepositoryOwnerId"/>,
/// every event it lists must carry a ref allow-list, and it may carry only the scopes in
/// <see cref="DeclarableScopes"/>. Anything broader — publishing or fetching a source — stays an
/// explicit act of a global admin on the instance. A declaration that breaks one rule is REFUSED
/// whole and named (<see cref="BuildPrincipalDeclarations.Refused"/>); it is never narrowed into
/// something that happens to pass.</para>
///
/// <para>🚨 <b>The node, when one exists, is the authority.</b> The authenticator reads
/// <c>Admin/_BuildPrincipal/&lt;id&gt;</c> first and consults the declaration only when no node is
/// there. So a hand-created node keeps working exactly as before, and a global admin can revoke a
/// DECLARED principal at once — write the node with <see cref="BuildPrincipalActions.Revoke"/> —
/// without waiting for a configuration change to roll.</para>
///
/// <para><b>Nothing is materialised and nothing is reconciled.</b> The declaration is read from
/// configuration when a token is decided; no node is written at boot, so there is no importer that
/// could prune one. Removing the declaration removes the trust at the next roll, and only that.</para>
///
/// <para>The shape, as environment variables on a portal (one index per repository):</para>
/// <code>
/// Plugins__Registry__BuildPrincipals__0__Repository=Owner/Name
/// Plugins__Registry__BuildPrincipals__0__RepositoryId=&lt;repository_id&gt;
/// Plugins__Registry__BuildPrincipals__0__RepositoryOwnerId=&lt;repository_owner_id&gt;
/// Plugins__Registry__BuildPrincipals__0__Events__workflow_run__0=verify
/// Plugins__Registry__BuildPrincipals__0__EventRefs__workflow_run__0=refs/heads/main
/// Plugins__Registry__BuildPrincipals__0__Scopes__0=verify:combo
/// </code>
/// </summary>
public static class DeclaredBuildPrincipals
{
    /// <summary>Configuration key holding the single accepted audience — usually the instance's own
    /// public URL. Unset (and no <see cref="AudiencesConfigSection"/>) disables the build-principal
    /// leg entirely, declared principals included.</summary>
    public const string AudienceConfigKey = "Plugins:Registry:BuildPrincipalAudience";

    /// <summary>Configuration section holding several accepted audiences.</summary>
    public const string AudiencesConfigSection = "Plugins:Registry:BuildPrincipalAudiences";

    /// <summary>Configuration section holding the declared principals, one child per repository.</summary>
    public const string ConfigSection = "Plugins:Registry:BuildPrincipals";

    /// <summary>The name of the <c>/health</c> entry that publishes what is declared.</summary>
    public const string HealthCheckName = "build_principals";

    /// <summary>
    /// The only scopes a declaration may carry. Deliberately a short, closed list: a declaration is
    /// applied to every instance of an estate by one merge, so it may grant only what is safe to
    /// grant that widely. <c>verify:combo</c> reads an instance's release inputs and records a
    /// compatibility verdict; it installs nothing and reads no user content.
    /// </summary>
    public static readonly ImmutableArray<string> DeclarableScopes =
        [BuildPrincipal.Scope(BuildVerbs.Verify, "combo")];

    private static readonly ImmutableHashSet<string> KnownKeys = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        nameof(BuildPrincipal.Repository), nameof(BuildPrincipal.RepositoryId),
        nameof(BuildPrincipal.RepositoryOwnerId), nameof(BuildPrincipal.Events),
        nameof(BuildPrincipal.EventRefs), nameof(BuildPrincipal.Scopes), nameof(BuildPrincipal.Notes));

    /// <summary>The audiences configured for the build-principal leg, de-duplicated and trimmed.
    /// EMPTY disables the leg.</summary>
    /// <param name="configuration">The deployment's configuration, or null.</param>
    /// <returns>The accepted audiences; empty when none is configured.</returns>
    public static ImmutableList<string> Audiences(IConfiguration? configuration)
    {
        if (configuration is null)
            return [];
        var candidates = new[] { configuration[AudienceConfigKey] }
            .Concat(configuration.GetSection(AudiencesConfigSection).GetChildren().Select(c => c.Value));
        var accepted = ImmutableList<string>.Empty;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            var value = candidate.Trim();
            if (!accepted.Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase)))
                accepted = accepted.Add(value);
        }
        return accepted;
    }

    /// <summary>
    /// Reads and validates every declaration under <see cref="ConfigSection"/>. Pure: the same
    /// configuration always yields the same answer, and a declaration that fails a rule is in
    /// <see cref="BuildPrincipalDeclarations.Refused"/> with the rule it failed.
    /// </summary>
    /// <param name="configuration">The deployment's configuration, or null.</param>
    /// <returns>The accepted and the refused declarations; both empty when none is declared.</returns>
    public static BuildPrincipalDeclarations Read(IConfiguration? configuration)
    {
        if (configuration is null)
            return BuildPrincipalDeclarations.None;

        var accepted = ImmutableList<DeclaredBuildPrincipal>.Empty;
        var refused = ImmutableList<RefusedBuildPrincipalDeclaration>.Empty;
        foreach (var section in configuration.GetSection(ConfigSection).GetChildren())
        {
            var path = section.Path;
            var repository = GitHubActionsToken.NormalizeRepository(section[nameof(BuildPrincipal.Repository)]);
            var (principal, reason) = Parse(section, repository, path);
            if (principal is null)
                refused = refused.Add(new RefusedBuildPrincipalDeclaration(path, repository, reason!));
            else
                accepted = accepted.Add(new DeclaredBuildPrincipal(path, principal));
        }

        // Two declarations for one repository are ambiguous trust: which one decides would depend on
        // enumeration order. Neither does.
        var twice = accepted
            .GroupBy(d => d.Principal.Repository, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToImmutableList();
        foreach (var duplicate in twice)
        {
            accepted = accepted.Remove(duplicate);
            refused = refused.Add(new RefusedBuildPrincipalDeclaration(
                duplicate.ConfigPath, duplicate.Principal.Repository,
                "the repository is declared more than once"));
        }

        return new BuildPrincipalDeclarations(accepted, refused);
    }

    /// <summary>
    /// The sentence the <c>/health</c> entry prints, and whether it is a problem. Printed whatever
    /// the status, because "nothing is declared", "one is declared" and "one is declared and
    /// refused" are three different facts and a declared trust must be visible to a reader who cannot
    /// search the Admin partition.
    /// </summary>
    /// <param name="declarations">What <see cref="Read"/> returned.</param>
    /// <param name="audiences">What <see cref="Audiences"/> returned.</param>
    /// <returns>Whether the reading is degraded, and its description.</returns>
    public static (bool Degraded, string Description) Describe(
        BuildPrincipalDeclarations declarations, IReadOnlyCollection<string> audiences)
    {
        if (declarations.Accepted.IsEmpty && declarations.Refused.IsEmpty)
            return (false,
                $"no build principal is DECLARED in configuration ({ConfigSection}); a build is trusted "
                + $"here only through a node under {BuildPrincipal.Namespace}, which this entry does not list");

        var parts = ImmutableList<string>.Empty;
        if (!declarations.Accepted.IsEmpty)
            parts = parts.Add(
                $"{declarations.Accepted.Count} build principal(s) DECLARED in configuration: "
                + string.Join("; ", declarations.Accepted.Select(d => Line(d.Principal)))
                + $". A node at {BuildPrincipal.Namespace}/<id> overrides its declaration, a revoke included; "
                + "nodes are not listed here");
        if (!declarations.Refused.IsEmpty)
            parts = parts.Add(
                $"{declarations.Refused.Count} declaration(s) REFUSED and trusted for nothing: "
                + string.Join("; ", declarations.Refused.Select(r =>
                    $"{r.ConfigPath}{(r.Repository.Length == 0 ? "" : $" ({r.Repository})")}: {r.Reason}")));
        var noAudience = !declarations.Accepted.IsEmpty && audiences.Count == 0;
        if (noAudience)
            parts = parts.Add(
                $"NO audience is configured ({AudienceConfigKey}), so every build token is refused and "
                + "the declared principal(s) cannot authenticate");

        return (!declarations.Refused.IsEmpty || noAudience, string.Join(". ", parts) + ".");
    }

    private static string Line(BuildPrincipal principal) =>
        $"{principal.Repository} [repository id {principal.RepositoryId}, owner id {principal.RepositoryOwnerId}] "
        + string.Join(", ", principal.Events
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => $"{e.Key}@{string.Join("|", principal.EventRefs[e.Key])}→{string.Join("|", e.Value)}"))
        + $" scopes {string.Join(",", principal.Scopes)}";

    private static (BuildPrincipal? Principal, string? Reason) Parse(
        IConfigurationSection section, string repository, string path)
    {
        var unknown = section.GetChildren()
            .Select(c => c.Key)
            .Where(k => !KnownKeys.Contains(k))
            .ToImmutableList();
        if (!unknown.IsEmpty)
            return (null, $"unknown key(s) {string.Join(", ", unknown)} — a declaration is read whole or not at all");

        var slash = repository.IndexOf('/');
        if (slash <= 0 || slash == repository.Length - 1 || repository.IndexOf('/', slash + 1) >= 0)
            return (null, "Repository must be 'owner/name'");

        var repositoryId = section[nameof(BuildPrincipal.RepositoryId)]?.Trim() ?? "";
        var ownerId = section[nameof(BuildPrincipal.RepositoryOwnerId)]?.Trim() ?? "";
        if (!IsNumericId(repositoryId))
            return (null, "RepositoryId (GitHub's immutable repository_id) is required and must be numeric");
        if (!IsNumericId(ownerId))
            return (null, "RepositoryOwnerId (GitHub's immutable repository_owner_id) is required and must be numeric");

        var scopes = Values(section.GetSection(nameof(BuildPrincipal.Scopes)))
            .Select(s => s.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToImmutableList();
        if (scopes.IsEmpty)
            return (null, "Scopes is empty");
        var undeclarable = scopes.Where(s => !DeclarableScopes.Contains(s, StringComparer.Ordinal)).ToImmutableList();
        if (!undeclarable.IsEmpty)
            return (null,
                $"scope(s) {string.Join(", ", undeclarable)} cannot be declared in configuration (only "
                + $"{string.Join(", ", DeclarableScopes)} can) — a global admin grants them on a node");
        var scopeVerbs = scopes.Select(s => s[..s.IndexOf(':')]).ToImmutableHashSet(StringComparer.Ordinal);

        var events = Map(section.GetSection(nameof(BuildPrincipal.Events)));
        if (events.IsEmpty)
            return (null, "Events is empty");
        var refs = Map(section.GetSection(nameof(BuildPrincipal.EventRefs)));
        foreach (var (eventName, verbs) in events)
        {
            if (verbs.IsEmpty)
                return (null, $"event '{eventName}' lists no verb");
            var foreign = verbs.Where(v => !scopeVerbs.Contains(v.ToLowerInvariant())).ToImmutableList();
            if (!foreign.IsEmpty)
                return (null,
                    $"event '{eventName}' lists verb(s) {string.Join(", ", foreign)} that no declared scope carries");
            if (!refs.TryGetValue(eventName, out var eventRefs) || eventRefs.IsEmpty)
                return (null, $"event '{eventName}' carries no EventRefs allow-list — a declared event must be pinned to refs");
            var notARef = eventRefs.Where(r => !r.StartsWith("refs/", StringComparison.Ordinal)).ToImmutableList();
            if (!notARef.IsEmpty)
                return (null, $"event '{eventName}' pins {string.Join(", ", notARef)}, which is not a full git ref (refs/…)");
        }
        var strayRefs = refs.Keys.Where(k => !events.ContainsKey(k)).ToImmutableList();
        if (!strayRefs.IsEmpty)
            return (null, $"EventRefs names event(s) {string.Join(", ", strayRefs)} that Events does not list");

        return (new BuildPrincipal
        {
            Repository = repository,
            RepositoryId = repositoryId,
            RepositoryOwnerId = ownerId,
            Events = events.ToImmutableDictionary(
                e => e.Key, e => (IReadOnlyCollection<string>)e.Value, StringComparer.OrdinalIgnoreCase),
            EventRefs = refs.ToImmutableDictionary(
                e => e.Key, e => (IReadOnlyCollection<string>)e.Value, StringComparer.OrdinalIgnoreCase),
            Scopes = scopes,
            IssuedBy = $"declared in configuration ({path})",
            Notes = section[nameof(BuildPrincipal.Notes)],
        }, null);
    }

    private static bool IsNumericId(string value) =>
        value.Length > 0 && value.All(c => c is >= '0' and <= '9');

    /// <summary>The non-blank values of a list section — its children, or its own scalar value.</summary>
    private static ImmutableList<string> Values(IConfigurationSection section)
    {
        var values = section.GetChildren().Select(c => c.Value);
        if (!string.IsNullOrWhiteSpace(section.Value))
            values = values.Prepend(section.Value);
        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToImmutableList();
    }

    private static ImmutableDictionary<string, ImmutableList<string>> Map(IConfigurationSection section) =>
        section.GetChildren().ToImmutableDictionary(
            c => c.Key, Values, StringComparer.OrdinalIgnoreCase);
}

/// <summary>One accepted declaration: where it was read from and the principal it yields.</summary>
/// <param name="ConfigPath">The configuration path of the declaration — what the log and the
/// entitlement ledger name instead of a node path.</param>
/// <param name="Principal">The principal, with both id pins, a ref allow-list per event and only
/// declarable scopes.</param>
public sealed record DeclaredBuildPrincipal(string ConfigPath, BuildPrincipal Principal);

/// <summary>One declaration that broke a rule and is therefore trusted for nothing.</summary>
/// <param name="ConfigPath">The configuration path of the declaration.</param>
/// <param name="Repository">The repository it named, normalized; empty when it named none.</param>
/// <param name="Reason">The rule it broke.</param>
public sealed record RefusedBuildPrincipalDeclaration(string ConfigPath, string Repository, string Reason);

/// <summary>What a deployment's configuration declares: the principals that passed every rule and
/// the declarations that did not.</summary>
/// <param name="Accepted">The declarations in force.</param>
/// <param name="Refused">The declarations refused, each with its reason.</param>
public sealed record BuildPrincipalDeclarations(
    ImmutableList<DeclaredBuildPrincipal> Accepted,
    ImmutableList<RefusedBuildPrincipalDeclaration> Refused)
{
    /// <summary>Nothing declared.</summary>
    public static BuildPrincipalDeclarations None { get; } = new([], []);

    /// <summary>The accepted declaration for <paramref name="repository"/>, matched exactly on the
    /// normalized <c>owner/name</c> — never a prefix — or null.</summary>
    /// <param name="repository">A token's <c>repository</c> claim.</param>
    /// <returns>The declaration, or null when this repository is not declared.</returns>
    public DeclaredBuildPrincipal? Find(string? repository) =>
        Accepted.FirstOrDefault(d => GitHubActionsToken.RepositoryEquals(d.Principal.Repository, repository));
}
