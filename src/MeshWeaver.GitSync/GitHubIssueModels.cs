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
