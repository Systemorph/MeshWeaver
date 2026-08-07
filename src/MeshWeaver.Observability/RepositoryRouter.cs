namespace MeshWeaver.Observability;

/// <summary>
/// Decides which repository an incident's ticket belongs in: the configured category-prefix route,
/// optionally overridden by the triage agent when the stack trace clearly blames someone else.
///
/// <para>Pure and synchronous — routing is a decision over configuration, not I/O, so it is
/// unit-testable on its own and cannot deadlock a hub.</para>
/// </summary>
public static class RepositoryRouter
{
    /// <summary>The outcome of routing one incident.</summary>
    /// <param name="Repository">The chosen repository (<c>owner/name</c> or URL), or null when
    /// nothing matched and no default is configured.</param>
    /// <param name="Overridden">True when the agent's choice won over the configured route.</param>
    /// <param name="RejectedOverride">The agent's proposal, when it was refused (not allowed, or
    /// not in the allowlist). Recorded so a bad override is visible rather than silent.</param>
    public record Route(string? Repository, bool Overridden, string? RejectedOverride);

    /// <summary>
    /// Resolves the destination for <paramref name="incident"/>. The configured route is the
    /// default; the agent's <see cref="LogIncidentDraft.Repository"/> wins only when overrides are
    /// enabled AND the proposal is an allowed destination.
    /// </summary>
    public static Route Resolve(LogIncident incident, LogWatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(options);

        var routed = ByCategory(incident.Category, options);
        var proposed = Normalize(incident.Draft?.Repository);
        if (proposed is null || string.Equals(proposed, Normalize(routed), StringComparison.OrdinalIgnoreCase))
            return new Route(routed, Overridden: false, RejectedOverride: null);

        if (!options.AllowAgentRepositoryOverride || !IsAllowed(proposed, options))
            return new Route(routed, Overridden: false, RejectedOverride: proposed);

        return new Route(incident.Draft!.Repository, Overridden: true, RejectedOverride: null);
    }

    /// <summary>
    /// The configured repository for a log category: the LONGEST matching prefix, so a specific
    /// route (<c>MeshWeaver.Courses.</c>) beats the catch-all (<c>MeshWeaver.</c>) regardless of
    /// the order they were configured in. Falls back to
    /// <see cref="LogWatchOptions.DefaultRepository"/>.
    /// </summary>
    public static string? ByCategory(string? category, LogWatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(category))
            return options.DefaultRepository;

        return options.Routes
                   .Where(r => !string.IsNullOrEmpty(r.Prefix)
                               && !string.IsNullOrWhiteSpace(r.Repository)
                               && category.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(r => r.Prefix.Length)
                   .Select(r => r.Repository)
                   .FirstOrDefault()
               ?? options.DefaultRepository;
    }

    /// <summary>
    /// Whether the agent may file into <paramref name="repository"/>. An empty allowlist means
    /// "any repository this deployment already routes to" — the agent can redirect between known
    /// destinations but can never invent a new one.
    /// </summary>
    public static bool IsAllowed(string repository, LogWatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var candidate = Normalize(repository);
        if (candidate is null)
            return false;

        if (!options.AllowedRepositories.IsEmpty)
            return options.AllowedRepositories.Any(
                r => string.Equals(Normalize(r), candidate, StringComparison.OrdinalIgnoreCase));

        return options.Routes.Any(
                   r => string.Equals(Normalize(r.Repository), candidate, StringComparison.OrdinalIgnoreCase))
               || string.Equals(Normalize(options.DefaultRepository), candidate, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reduces a repository reference to a comparable <c>owner/name</c>: full URLs, a trailing
    /// <c>.git</c>, and casing differences all collapse, so <c>https://github.com/Systemorph/MeshWeaver.git</c>
    /// and <c>Systemorph/MeshWeaver</c> compare equal.
    /// </summary>
    public static string? Normalize(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return null;
        var value = repository.Trim().TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        var host = value.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
        if (host >= 0)
            value = value[(host + "github.com".Length)..].TrimStart('/', ':');
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            ? $"{segments[^2]}/{segments[^1]}"
            : null;
    }

    /// <summary>The full clone/API URL for a repository reference, as the GitHub client expects.</summary>
    public static string? ToUrl(string? repository)
    {
        var normalized = Normalize(repository);
        return normalized is null ? null : $"https://github.com/{normalized}";
    }
}
