using System.ComponentModel;

namespace MeshWeaver.GitSync;

/// <summary>
/// The lifecycle state of a pull request. This is GitHub-owned state — it is read LIVE
/// from GitHub (delegated), never persisted as an authoritative field on the node (that
/// would drift). <see cref="Draft"/> is the local-only state before the PR is opened.
/// </summary>
public enum PullRequestStatus
{
    /// <summary>Drafted locally (AI-suggested + user-edited), not yet opened on GitHub.</summary>
    Draft,

    /// <summary>Open on GitHub.</summary>
    Open,

    /// <summary>Merged on GitHub.</summary>
    Merged,

    /// <summary>Closed on GitHub without merging.</summary>
    Closed,
}

/// <summary>
/// A pull-request DRAFT for a Space, stored as a satellite MeshNode at
/// <c>{spacePath}/_PullRequest/{id}</c> (NodeType <c>PullRequest</c>). This is the
/// data-binding anchor for the "Open pull request" GUI.
///
/// <para>🚨 <b>It does NOT replicate GitHub state.</b> The node holds only:
/// (1) the <b>local draft authoring state</b> the user edits before the PR exists —
/// <see cref="Title"/>, <see cref="Body"/>, <see cref="HeadBranch"/>,
/// <see cref="BaseBranch"/>; and (2) the <b>immutable GitHub handle</b> assigned once on
/// open — <see cref="Number"/> + <see cref="Url"/>. The handle is how we delegate back to
/// GitHub to ask the live state; it is identity, not mutable state. The drift-prone
/// lifecycle status (open / merged / closed) is read LIVE from GitHub on demand
/// (<c>PullRequestService.AskStatus</c>) and is never stored here.</para>
/// </summary>
public record GitHubPullRequest
{
    /// <summary>The PR title — AI-drafted, then user-editable.</summary>
    [Description("Title")]
    public string? Title { get; init; }

    /// <summary>The PR body (markdown) — AI-drafted, then user-editable.</summary>
    [Description("Body (markdown)")]
    public string? Body { get; init; }

    /// <summary>The branch the changes live on (the PR's head).</summary>
    [Description("Head branch")]
    public string? HeadBranch { get; init; }

    /// <summary>The branch the PR targets (the PR's base). Defaults to <c>main</c>.</summary>
    [Description("Base branch")]
    public string BaseBranch { get; init; } = "main";

    /// <summary>
    /// The GitHub PR number, assigned once on open — an immutable handle, NOT mutable state.
    /// Null until opened. Used to ask GitHub for the live status. Not user-editable.
    /// </summary>
    [Browsable(false)]
    public int? Number { get; init; }

    /// <summary>The <c>html_url</c> of the PR on GitHub, set once on open (immutable handle). Not user-editable.</summary>
    [Browsable(false)]
    public string? Url { get; init; }
}
