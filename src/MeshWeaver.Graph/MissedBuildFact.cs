namespace MeshWeaver.Graph;

/// <summary>
/// The durable record that a green build was reported and NOT recorded — the fact
/// <see cref="BuildCompletion"/> should have carried, kept where someone can find it
/// (Systemorph/MeshWeaver#3374).
///
/// <para><b>What was losing it.</b> The <c>workflow_run</c> webhook writes
/// <c>Admin/_Build/{owner}.{repo}</c>. When that write fails, the handler logs a warning, returns 0
/// and answers GitHub <b>200 OK</b>. The 200 is deliberate and right — a non-2xx makes GitHub
/// redeliver, and the comment guarding it anticipates that storm — but the consequence is that the
/// event is gone for good: the build record keeps its previous value, the sync the green build
/// authorises never runs (it hangs off the success branch), GitHub will not redeliver, and nothing
/// else retries. A transient infrastructure fault becomes permanent data loss. Measured on
/// memex-cloud: <b>154 of these in one week</b>, and two distinct inner causes — an owner that
/// returned no verdict, and initial state that never arrived within 30 s.</para>
///
/// <para><b>Why a node and not just a louder log.</b> The log line is the floor, not the record.
/// On that deployment the ATTEMPT is logged at <c>Information</c> and <c>Information</c> is not
/// emitted, so the failure line has no denominator — 154 failures could be 5 % of deliveries or
/// 100 % and nothing can tell them apart. A node is queryable, survives the pod, and carries the
/// payload verbatim, so the lost build can be replayed rather than merely mourned.</para>
///
/// <para>🚨 <b>Being "still missing" is a READ, not a second write.</b> There is deliberately no
/// drain, no <c>Pending</c> flag to clear and no timer. A later green build for the same repository
/// writes <c>Admin/_Build/{owner}.{repo}</c> and runs the sync, which supersedes the lost one
/// outright — so whether this record still matters is answered by comparing it against the build
/// record that exists now (<see cref="IsSupersededBy"/>). That is the same "end on a fact you can
/// read" shape <c>RegistryUpdateReconciler</c> settled on, and it costs the happy path nothing: a
/// green delivery that works writes no extra node, opens no query, and cannot fail in a new
/// way.</para>
///
/// <para><b>One node per repository, updated in place</b> — the same choice
/// <see cref="BuildCompletion"/> makes and for the same reason. A node per missed run would grow
/// without bound; the per-run history is already in the node's own version history, and the
/// question anyone actually asks is "is this repo currently missing a build fact?".</para>
/// </summary>
public record MissedBuildFact
{
    /// <summary>The node type registered for these records.</summary>
    public const string NodeType = "MissedBuildFact";

    /// <summary>The path segment missed-build records are anchored under, inside the Admin partition.</summary>
    public const string SatelliteSegment = "_MissedBuild";

    /// <summary>The namespace every missed-build record lives in: <c>Admin/_MissedBuild</c>.</summary>
    public const string Namespace = "Admin/" + SatelliteSegment;

    /// <summary>
    /// The query to WATCH or LIST every repository's missed-build record.
    ///
    /// <para>🚨 PATH-SCOPED for the same non-obvious reason <see cref="BuildCompletion.WatchQuery"/>
    /// is: these records live in the <b>Admin partition</b>, which an UNSCOPED query does not reach.
    /// A bare <c>nodeType:MissedBuildFact</c> answers EMPTY however many records exist — and it
    /// fails that way silently, so "no build facts have been missed" would be indistinguishable
    /// from "the query cannot see them". A record whose whole purpose is to make a silent loss
    /// visible must not be readable only through a query that is silently blind.</para>
    /// </summary>
    public const string WatchQuery = $"path:{Namespace} scope:children nodeType:{NodeType}";

    /// <summary>
    /// The canonical node path for one repository's missed-build record:
    /// <c>Admin/_MissedBuild/{owner}.{repo}</c> — deliberately the same
    /// <c>{owner}.{repo}</c> id as <see cref="BuildCompletion.PathFor"/>, so the two are trivially
    /// paired by a reader.
    /// </summary>
    /// <param name="owner">The repository owner (GitHub org or user).</param>
    /// <param name="repo">The repository name.</param>
    public static string PathFor(string owner, string repo)
        => $"{Namespace}/{Sanitize(owner)}.{Sanitize(repo)}";

    /// <summary>Path separators would fabricate node hierarchy, so they never survive into a path
    /// segment. Identical to <see cref="BuildCompletion"/>'s rule, and for the same reason: the
    /// value arrives from a webhook payload and is treated as untrusted.</summary>
    private static string Sanitize(string s) => s.Replace('/', '-').Replace('\\', '-');

    /// <summary>
    /// The build fact that was NOT recorded, verbatim — everything the write would have stored, so
    /// the record can be replayed rather than reconstructed from a log line.
    /// </summary>
    public BuildCompletion Build { get; init; } = new();

    /// <summary>When the write was attempted and failed (UTC).</summary>
    public DateTimeOffset MissedAt { get; init; }

    /// <summary>
    /// The write's own error text — the mesh's answer, not a summary of it. The two shapes seen in
    /// production are an owner that returned no verdict and initial state that never arrived, and
    /// they have different causes, so the distinction has to survive into the record.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Builds the record for a build fact that could not be written.
    /// </summary>
    /// <param name="build">The completion that was being recorded.</param>
    /// <param name="error">The failing write's error text, if it gave one.</param>
    /// <param name="missedAt">When the attempt failed (UTC).</param>
    public static MissedBuildFact For(BuildCompletion build, string? error, DateTimeOffset missedAt)
        => new() { Build = build, Error = error, MissedAt = missedAt };

    /// <summary>
    /// Whether <paramref name="current"/> — the build record that exists NOW for this repository —
    /// already covers what this record says was lost.
    ///
    /// <para>A later green build both writes the record and runs the sync the verdict authorises,
    /// so it supersedes the lost one outright and this record is history rather than a to-do.
    /// Compared on <see cref="BuildCompletion.RunNumber"/>, GitHub's monotonically incrementing
    /// per-workflow counter, because it is the only field here that ORDERS: a sha does not, and
    /// <see cref="BuildCompletion.CompletedAtUtc"/> is a remote clock that may be absent. A run
    /// number that is equal counts as superseding — the same run recorded on a retry is exactly the
    /// fact this record was holding.</para>
    ///
    /// <para>🚨 Different WORKFLOWS have independent run numbers, so a record is only superseded by
    /// a build of the same workflow. Comparing across workflows would let an unrelated workflow's
    /// higher counter silently retire a real gap.</para>
    /// </summary>
    /// <param name="current">The repository's current build record, or <c>null</c> when it has none.</param>
    /// <returns><c>true</c> when the lost fact no longer matters.</returns>
    public bool IsSupersededBy(BuildCompletion? current)
        => current is not null
           && string.Equals(current.WorkflowName, Build.WorkflowName, StringComparison.Ordinal)
           && current.RunNumber >= Build.RunNumber;
}
