using System.Collections.Immutable;

namespace MeshWeaver.Observability;

/// <summary>
/// Portal-side configuration for red-log ticketing, bound from the <c>LogWatch</c> configuration
/// section. The in-cluster watcher has its own, separate settings (Loki URL, poll interval) — this
/// section covers only what the portal decides: who may report, where tickets go, and how loud
/// recurrence is allowed to be.
/// </summary>
public sealed record LogWatchOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "LogWatch";

    /// <summary>
    /// The shared secret the watcher presents as <c>Authorization: Bearer …</c>. Ship it via
    /// KeyVault. Unset ⇒ the ingest endpoint is NOT mapped at all: an open endpoint would let any
    /// caller spend model budget and open issues, so absence means off, never open.
    /// </summary>
    public string? IngestToken { get; init; }

    /// <summary>
    /// The agent that triages incidents and drafts the ticket. Must match an Agent node's name.
    /// </summary>
    public string TriageAgent { get; init; } = "LogTriage";

    /// <summary>
    /// Repository to file into when no <see cref="Routes"/> prefix matches. <c>owner/name</c> or a
    /// full URL. Unset ⇒ unrouted incidents are ingested and triaged but parked at
    /// <see cref="LogIncidentStatus.Failed"/> rather than filed somewhere arbitrary.
    /// </summary>
    public string? DefaultRepository { get; init; }

    /// <summary>
    /// Log-category prefix → repository. Longest matching prefix wins, so
    /// <c>MeshWeaver.Courses</c> can be routed away from the <c>MeshWeaver</c> catch-all.
    /// </summary>
    public ImmutableList<LogWatchRoute> Routes { get; init; } = ImmutableList<LogWatchRoute>.Empty;

    /// <summary>
    /// Whether the triage agent may override the routed repository. When false the route is
    /// binding and an agent-proposed repository is ignored (but still recorded on the incident).
    /// </summary>
    public bool AllowAgentRepositoryOverride { get; init; } = true;

    /// <summary>
    /// Repositories the agent is allowed to override INTO. Empty ⇒ any repository named by a
    /// <see cref="Routes"/> entry or <see cref="DefaultRepository"/>. The agent can never invent a
    /// destination outside this set — an LLM-chosen repo is a write target, so it is allowlisted.
    /// </summary>
    public ImmutableList<string> AllowedRepositories { get; init; } = ImmutableList<string>.Empty;

    /// <summary>
    /// Minimum gap between recurrence comments on one issue. A fingerprint that fires continuously
    /// gets ONE "still happening" comment per window, not one per report.
    /// </summary>
    public TimeSpan CommentInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Reopen a closed issue when its fingerprint returns. On ⇒ a regression re-surfaces on the
    /// original ticket instead of silently recurring under a closed one.
    /// </summary>
    public bool ReopenOnRecurrence { get; init; } = true;

    /// <summary>How many evidence lines an incident keeps. Older samples roll off.</summary>
    public int MaxSamples { get; init; } = 10;

    /// <summary>Labels applied to every filed issue, on top of whatever the agent proposes.</summary>
    public ImmutableList<string> Labels { get; init; } = ImmutableList<string>.Empty;
}

/// <summary>One category-prefix → repository route.</summary>
public sealed record LogWatchRoute
{
    /// <summary>The log-category prefix, e.g. <c>MeshWeaver.</c> or <c>Memex.</c>.</summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>The repository to file into, as <c>owner/name</c> or a full URL.</summary>
    public string Repository { get; init; } = string.Empty;
}
