using System.Collections.Immutable;
using System.ComponentModel;

namespace MeshWeaver.GitSync;

/// <summary>The lifecycle state of a GitHub issue.</summary>
public enum GitHubIssueState
{
    /// <summary>The issue is open on GitHub.</summary>
    Open,

    /// <summary>The issue is closed on GitHub.</summary>
    Closed,
}

/// <summary>
/// WHY a GitHub issue is closed — the <c>state_reason</c> GitHub records beside the state.
///
/// <para>🚨 <b>A close is a decision, and <see cref="GitHubIssueState.Closed"/> does not carry
/// it.</b> "Fixed", "won't do" and "this is tracked elsewhere" are three different statements about
/// the same closed issue, and a caller that can only read the state treats them identically —
/// which is how an automated recurrence reverted a human's deliberate consolidation
/// (Systemorph/MeshWeaver.Plugins#2177).</para>
///
/// <para>🚨 <b>Deliberately NOT Octokit's <c>ItemStateReason</c>, and not for tidiness.</b> That
/// enum has three members — <c>Completed</c>, <c>NotPlanned</c>, <c>Reopened</c> — and GitHub
/// serves a fourth value, <c>duplicate</c>. Octokit models the field as
/// <c>StringEnum&lt;ItemStateReason&gt;?</c>, whose <c>.Value</c> THROWS
/// <c>ArgumentException: Value 'duplicate' is not a valid 'ItemStateReason' enum value</c>
/// (measured against Octokit 14.0.0). So the one reason this type exists to express is the one
/// reason the vendor enum cannot hold, and reading it the obvious way faults on it. This enum is
/// parsed from the RAW string by <see cref="GitHubIssueStateReasons.Parse"/>, is total, and answers
/// <see cref="Unknown"/> for a value GitHub has not taught it yet rather than throwing.</para>
/// </summary>
public enum GitHubIssueStateReason
{
    /// <summary>
    /// GitHub reported no reason, or a value this build does not know.
    ///
    /// <para>🚨 It is the default on purpose, and it means <b>"nothing was established"</b>, never
    /// "closed as completed". A caller deciding anything on the reason must treat it as an absence
    /// of evidence: GitHub omits <c>state_reason</c> entirely for an OPEN issue and for issues
    /// closed before it existed, and a future value would land here too.</para>
    /// </summary>
    Unknown,

    /// <summary>Closed as done — the work landed.</summary>
    Completed,

    /// <summary>Closed as not planned — a decision that it will not be done.</summary>
    NotPlanned,

    /// <summary>
    /// Closed as a duplicate — a decision that the subject is tracked on another issue.
    ///
    /// <para>🚨 The value Octokit's own <c>ItemStateReason</c> does not have; see the type remarks.</para>
    /// </summary>
    Duplicate,

    /// <summary>Reopened. GitHub keeps the reason on an issue that was closed and reopened.</summary>
    Reopened,
}

/// <summary>Reads GitHub's <c>state_reason</c> wire value into <see cref="GitHubIssueStateReason"/>.</summary>
public static class GitHubIssueStateReasons
{
    /// <summary>
    /// The wire value as a <see cref="GitHubIssueStateReason"/>. TOTAL: null, empty, and any value
    /// this build does not know all answer <see cref="GitHubIssueStateReason.Unknown"/> — never an
    /// exception, which is the whole difference from Octokit's <c>StringEnum.Value</c>.
    /// </summary>
    /// <param name="wireValue">GitHub's <c>state_reason</c>, e.g. <c>duplicate</c>. May be null.</param>
    /// <returns>The parsed reason, or <see cref="GitHubIssueStateReason.Unknown"/>.</returns>
    public static GitHubIssueStateReason Parse(string? wireValue) => wireValue?.Trim() switch
    {
        null or "" => GitHubIssueStateReason.Unknown,
        var v when v.Equals("completed", StringComparison.OrdinalIgnoreCase)
            => GitHubIssueStateReason.Completed,
        // GitHub's wire spelling is snake_case; Octokit's StringEnum round-trips the same token.
        var v when v.Equals("not_planned", StringComparison.OrdinalIgnoreCase)
                   || v.Equals("notplanned", StringComparison.OrdinalIgnoreCase)
            => GitHubIssueStateReason.NotPlanned,
        var v when v.Equals("duplicate", StringComparison.OrdinalIgnoreCase)
            => GitHubIssueStateReason.Duplicate,
        var v when v.Equals("reopened", StringComparison.OrdinalIgnoreCase)
            => GitHubIssueStateReason.Reopened,
        _ => GitHubIssueStateReason.Unknown,
    };
}

/// <summary>
/// A GitHub issue mirrored into the Space as a satellite MeshNode at
/// <c>{spacePath}/_Issue/{number}</c> (NodeType <c>GitHubIssue</c>). Unlike a pull request
/// (whose live status is delegated), issues are content the user asked to <b>sync in</b> —
/// so the node is a point-in-time snapshot refreshed by an explicit sync or a live webhook
/// (see <c>GitHubWebhookProcessor</c>). It is display-only; edits to issues happen on
/// GitHub (create / comment / close), never by mutating this node directly.
/// </summary>
public record GitHubIssue
{
    /// <summary>The GitHub issue number (the immutable handle, stable across syncs).</summary>
    [Browsable(false)]
    public int Number { get; init; }

    /// <summary>The issue title.</summary>
    [Description("Title")]
    public string? Title { get; init; }

    /// <summary>The issue body (markdown).</summary>
    [Description("Body")]
    public string? Body { get; init; }

    /// <summary>Open / Closed on GitHub.</summary>
    [Description("State")]
    public GitHubIssueState State { get; init; }

    /// <summary>The login of the user who opened the issue.</summary>
    [Description("Author")]
    public string? AuthorLogin { get; init; }

    /// <summary>The issue's label names.</summary>
    public ImmutableList<string> Labels { get; init; } = ImmutableList<string>.Empty;

    /// <summary>The logins of the users assigned to the issue.</summary>
    public ImmutableList<string> Assignees { get; init; } = ImmutableList<string>.Empty;

    /// <summary>The number of comments on the issue.</summary>
    public int CommentsCount { get; init; }

    /// <summary>The <c>html_url</c> of the issue on GitHub.</summary>
    [Browsable(false)]
    public string? Url { get; init; }

    /// <summary>When the issue was opened on GitHub.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When the issue was last updated on GitHub.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>When the issue was closed on GitHub (null while open).</summary>
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>
    /// WHY the issue is closed, when GitHub said — <c>completed</c>, <c>not_planned</c> or
    /// <c>duplicate</c>.
    ///
    /// <para>🚨 <see cref="GitHubIssueStateReason.Unknown"/> is the default and means the reason was
    /// NOT established: an open issue carries none, GitHub omits it for issues closed before the
    /// field existed, and a list read that never asked lands here too. It is not a synonym for
    /// <see cref="GitHubIssueStateReason.Completed"/>, and reading it as one is the failure this
    /// field exists to prevent.</para>
    /// </summary>
    public GitHubIssueStateReason StateReason { get; init; }

    /// <summary>The issue's comments, populated on a detailed sync (empty on a list sync).</summary>
    public ImmutableList<GitHubIssueComment> Comments { get; init; } = ImmutableList<GitHubIssueComment>.Empty;

    /// <summary>
    /// Whether <see cref="Comments"/> is the WHOLE comment list, or merely everything that could be
    /// read (Systemorph/MeshWeaver#4629).
    ///
    /// <para>🚨 An empty <see cref="Comments"/> means "this issue has no comments" only while this
    /// is <c>true</c>. GitHub's transfer redirect covers the issue resource and NOT its
    /// sub-resources, so a TRANSFERRED issue answers 200 for itself and 404 for its comments —
    /// measured 2026-09-17 on <c>Systemorph/MeshWeaver#2950</c>, which now lives at
    /// <c>Systemorph/MeshWeaver.Plugins#1139</c>. The read that produced that pair is a successful
    /// read of a real issue, and discarding it is what made a transferred issue indistinguishable
    /// from a deleted one for every caller.</para>
    ///
    /// <para>🚨 Defaults to <c>false</c>, and the direction is the whole point: MOST reads never ask
    /// for comments at all — <c>ListIssues</c> maps every row through the same projection and leaves
    /// <see cref="Comments"/> empty. A default of <c>true</c> would have those list snapshots claim
    /// their empty list is the whole story, which is the same confusion one level up. Only the read
    /// that actually received a comment list sets this, so "complete" is always something a reader
    /// EARNED rather than something it inherited. This is the opposite default from
    /// <c>RepoSnapshot.ListingIsComplete</c>, for the opposite reason: a snapshot is normally built
    /// from a full clone and is complete unless told otherwise, while an issue normally arrives
    /// without its comments.</para>
    /// </summary>
    public bool CommentsAreComplete { get; init; }
}

/// <summary>A single comment on a GitHub issue (or pull request — a PR is an issue).</summary>
public record GitHubIssueComment(
    long Id,
    string? AuthorLogin,
    string? Body,
    DateTimeOffset? CreatedAt,
    string? Url);

/// <summary>A request to open a new issue on GitHub.</summary>
public record GitHubCreateIssueRequest
{
    /// <summary>The repository URL the issue is opened in.</summary>
    public required string RepositoryUrl { get; init; }

    /// <summary>The issue title.</summary>
    public required string Title { get; init; }

    /// <summary>The issue body (markdown); null for no description.</summary>
    public string? Body { get; init; }

    /// <summary>Optional label names to apply to the new issue.</summary>
    public ImmutableList<string> Labels { get; init; } = ImmutableList<string>.Empty;

    /// <summary>The committing user's OAuth access token (decrypted).</summary>
    public required string AccessToken { get; init; }
}
