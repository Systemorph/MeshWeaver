using System.Collections.Immutable;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// How long an <see cref="ActivationRecycleRequest"/> at <c>Admin/_Recycle/{id}</c> — and with it
/// every per-process <see cref="ActivationRecycleReport"/> under it — is kept, and how many one
/// prune may remove. The whole policy: the selection <see cref="SelectForPruning"/> makes and the
/// bounds it works within, with no mesh, no hub and no clock of its own.
///
/// <para><b>Why requests get a retention.</b> A request plus its reports is the audit of a recycle:
/// who asked, when, why, which types, and what each process disposed. Nothing ever removed one, and
/// once every NodeType definition recycle issues a request, the rows grow with every recycle, module
/// landing and deploy (Systemorph/MeshWeaver#6813).</para>
///
/// <para><b>Who prunes.</b> The writer does, at the moment it writes a new request:
/// <see cref="ActivationRecycle.Request"/> prunes right after its create, the same owner-prunes-on-write
/// shape as <c>CompileActivityRetention</c>. No timer and no sweeper: the rows grow only through
/// that write, so pruning on it bounds them, and a mesh that stops recycling stops growing. Each
/// prune is capped at <see cref="MaxDeletionsPerRun"/>, so a backlog drains over successive
/// requests.</para>
///
/// <para>Declared, not derived: every bound comes from configuration
/// (<see cref="FromConfiguration"/>) with the shipped values as the fallback.</para>
/// </summary>
public sealed record ActivationRecycleRetention
{
    /// <summary>Config key turning the prune off entirely. Default <c>true</c>.</summary>
    public const string EnabledConfigKey = "ActivationRecycle:Retention:Enabled";

    /// <summary>Config key overriding <see cref="MaxAge"/> (a <see cref="TimeSpan"/> string, e.g. <c>30.00:00:00</c>).</summary>
    public const string MaxAgeConfigKey = "ActivationRecycle:Retention:MaxAge";

    /// <summary>Config key overriding <see cref="MaxDeletionsPerRun"/>.</summary>
    public const string MaxDeletionsPerRunConfigKey = "ActivationRecycle:Retention:MaxDeletionsPerRun";

    /// <summary>
    /// The floor <see cref="FromConfiguration"/> clamps a CONFIGURED <see cref="MaxAge"/> to. A
    /// window of zero in a chart is a typo, not a request to delete the audit of the recycle that
    /// just ran. The clamp lives at the configuration edge only; a directly constructed policy is
    /// applied exactly as written.
    /// </summary>
    public static readonly TimeSpan MinimumMaxAge = TimeSpan.FromDays(1);

    /// <summary>The shipped policy: 30 days, 50 removals per prune, enabled.</summary>
    public static readonly ActivationRecycleRetention Default = new();

    /// <summary>Whether a prune removes anything at all. Off means no query is issued.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// A request (and its reports) is removed once its node has not been written for this long.
    ///
    /// <para>Thirty days: long enough that the audit of a recent deploy or incident is still there
    /// when someone reads it — a post-incident review, or the question "which silos actually
    /// recycled after last week's release" — and a full monthly release cycle back. Short enough
    /// that the rows stay bounded by one month of recycle traffic instead of the portal's whole
    /// history.</para>
    /// </summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// The cap on how many requests ONE prune removes (each with its reports). A backlog drains by
    /// this many per new request, instead of one request turning into a mass delete.
    /// </summary>
    public int MaxDeletionsPerRun { get; init; } = 50;

    /// <summary>
    /// The query that reads the oldest window of requests. It bounds the work; the decision is
    /// <see cref="SelectForPruning"/>'s, so a backend that ignored the sort would only drain slower,
    /// never delete a row the policy has not called expired. One more than the cap, so the request
    /// the caller just wrote never crowds out a row that may go.
    /// </summary>
    public string WindowQuery
        => $"path:{ActivationRecycleRequest.Namespace} scope:children nodeType:{ActivationRecycleRequest.NodeType} "
           + $"sort:LastModified-asc limit:{MaxDeletionsPerRun + 1}";

    /// <summary>
    /// Whether <paramref name="node"/> is a request this policy removes at <paramref name="now"/>.
    /// The clock is <see cref="MeshNode.LastModified"/> — the quantity the window query orders by.
    ///
    /// <para>🚨 Fail closed: not a request, not directly under <c>Admin/_Recycle</c>, or undated ⇒
    /// kept. This decides what is deleted, so every uncertainty resolves to keeping the row.</para>
    /// </summary>
    /// <param name="node">The candidate row.</param>
    /// <param name="now">The current time, passed in so the rule is a pure function.</param>
    public bool IsExpired(MeshNode? node, DateTimeOffset now)
    {
        if (!Enabled || node is null)
            return false;
        if (!string.Equals(node.NodeType, ActivationRecycleRequest.NodeType, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(node.Namespace?.Trim('/'), ActivationRecycleRequest.Namespace, StringComparison.OrdinalIgnoreCase))
            return false;
        if (node.LastModified == default)
            return false;
        return node.LastModified <= now - MaxAge;
    }

    /// <summary>
    /// The request paths among <paramref name="requests"/> this policy removes at
    /// <paramref name="now"/>: expired ones, oldest first, never <paramref name="keepPath"/>, at most
    /// <see cref="MaxDeletionsPerRun"/>.
    /// </summary>
    /// <param name="requests">Candidate rows, in any order.</param>
    /// <param name="now">The current time.</param>
    /// <param name="keepPath">The request the caller just wrote; never selected.</param>
    public ImmutableList<string> SelectForPruning(IEnumerable<MeshNode> requests, DateTimeOffset now, string? keepPath = null)
        => !Enabled
            ? ImmutableList<string>.Empty
            : requests
                .Where(n => IsExpired(n, now))
                .Where(n => !string.Equals(n.Path, keepPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.LastModified)
                .ThenBy(n => n.Id, StringComparer.Ordinal)
                .Select(n => n.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxDeletionsPerRun)
                .ToImmutableList();

    /// <summary>
    /// Reads the policy from configuration. Every key falls back to its default when absent or
    /// malformed; a configured window below <see cref="MinimumMaxAge"/> is clamped to it, so a typo
    /// never widens what gets deleted.
    /// </summary>
    /// <param name="configuration">The host's configuration, or null on a host that has none.</param>
    public static ActivationRecycleRetention FromConfiguration(IConfiguration? configuration)
    {
        var retention = Default;
        if (configuration is null)
            return retention;

        if (bool.TryParse(configuration[EnabledConfigKey], out var enabled))
            retention = retention with { Enabled = enabled };
        // Every PARSED span is clamped, zero and negative included: only an absent or unparseable
        // value falls back to the default.
        if (TimeSpan.TryParse(configuration[MaxAgeConfigKey], System.Globalization.CultureInfo.InvariantCulture, out var maxAge))
            retention = retention with { MaxAge = maxAge < MinimumMaxAge ? MinimumMaxAge : maxAge };
        if (int.TryParse(configuration[MaxDeletionsPerRunConfigKey], out var max) && max >= 1)
            retention = retention with { MaxDeletionsPerRun = max };

        return retention;
    }
}
