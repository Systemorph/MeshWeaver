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
