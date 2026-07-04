using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// GitHub <b>issue</b> integration for a Space: list/sync repo issues into satellite MeshNodes,
/// read a single issue with its comments, open new issues, and comment — all authenticated as
/// the calling user through their stored OAuth credential.
///
/// <para>Issues are the one GitHub object the user asked to <b>materialize</b> (read/sync into
/// the mesh): each is mirrored to <c>{spacePath}/_Issue/{number}</c> (NodeType
/// <c>GitHubIssue</c>) via the canonical create-or-update verb, and refreshed by an explicit
/// sync or a live webhook. Mutations (create / comment) run ON GitHub and then re-sync the
/// affected node — the node is never edited directly. Reactive end-to-end (no
/// <c>async</c>/<c>await</c>): every GitHub leaf is bridged through <c>IIoPool</c> inside
/// <see cref="OctokitGitHubRepoClient"/>; this service only composes those observables.</para>
/// </summary>
public sealed class IssueService
{
    /// <summary>The <see cref="MeshNode.NodeType"/> of a synced GitHub-issue node.</summary>
    public const string NodeType = "GitHubIssue";
    /// <summary>The satellite namespace segment under a Space that holds issue nodes.</summary>
    public const string SatelliteSegment = "_Issue";

    private readonly IMessageHub hub;
    private readonly IMeshService meshService;
    private readonly IGitHubRepoClient repoClient;
    private readonly GitHubCredentialService credentials;
    private readonly GitHubSyncService sync;
    private readonly ILogger? logger;

    /// <summary>Initializes a new instance of the <see cref="IssueService"/> class.</summary>
    public IssueService(
        IMessageHub hub,
        IMeshService meshService,
        IGitHubRepoClient repoClient,
        GitHubCredentialService credentials,
        GitHubSyncService sync,
        ILogger<IssueService>? logger = null)
    {
        this.hub = hub;
        this.meshService = meshService;
        this.repoClient = repoClient;
        this.credentials = credentials;
        this.sync = sync;
        this.logger = logger;
    }

    /// <summary>The satellite namespace under a Space that holds issue nodes: <c>{spacePath}/_Issue</c>.</summary>
    public static string IssueNamespace(string spacePath) => $"{spacePath}/{SatelliteSegment}";

    /// <summary>The node path for the issue with the given number: <c>{spacePath}/_Issue/{number}</c>.</summary>
    public static string IssuePath(string spacePath, int number) => $"{IssueNamespace(spacePath)}/{number}";

    // ══════════════════════════════════════════════════════════════════════════
    //  Sync (read GitHub → mesh nodes)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lists the configured repo's issues (optionally filtered by <paramref name="state"/>) and
    /// upserts one <c>{spacePath}/_Issue/{number}</c> node per issue. Emits the number synced.
    /// </summary>
    public IObservable<int> SyncIssues(string spacePath, string userId, GitHubIssueState? state = null)
    {
        return WithRepoAndToken(spacePath, userId, (repoUrl, _, token) =>
            repoClient.ListIssues(repoUrl, state, token).SelectMany(issues =>
            {
                if (issues.Count == 0)
                {
                    logger?.LogInformation("No issues to sync for {Space}.", spacePath);
                    return Observable.Return(0);
                }
                logger?.LogInformation("Syncing {Count} issue(s) into {Space}.", issues.Count, spacePath);
                return issues
                    .Select(issue => UpsertIssueNode(spacePath, issue))
                    .Merge(4)
                    .ToList()
                    .Select(list => list.Count);
            }));
    }

    /// <summary>
    /// Reads a single issue (with its comments) from GitHub and upserts its node — the
    /// detailed refresh used after a comment or from a webhook. Emits the refreshed node.
    /// </summary>
    public IObservable<MeshNode> SyncIssue(string spacePath, int number, string userId)
    {
        return WithRepoAndToken(spacePath, userId, (repoUrl, _, token) =>
            repoClient.GetIssue(repoUrl, number, token)
                .SelectMany(issue => UpsertIssueNode(spacePath, issue)));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Mutations (act on GitHub → re-sync node)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens a new issue on GitHub, then materializes its node. Emits the created node.
    /// </summary>
    public IObservable<MeshNode> CreateIssue(
        string spacePath, string title, string? body, IReadOnlyList<string>? labels, string userId)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Observable.Throw<MeshNode>(new InvalidOperationException("Enter a title for the issue."));
        return WithRepoAndToken(spacePath, userId, (repoUrl, _, token) =>
            repoClient.CreateIssue(new GitHubCreateIssueRequest
            {
                RepositoryUrl = repoUrl,
                Title = title.Trim(),
                Body = body,
                Labels = labels?.ToImmutableList() ?? ImmutableList<string>.Empty,
                AccessToken = token,
            }).SelectMany(issue => UpsertIssueNode(spacePath, issue)));
    }

    /// <summary>
    /// Posts a comment on an issue, then re-syncs that issue's node (so its comment count +
    /// comment list reflect the new comment). Emits the refreshed node.
    /// </summary>
    public IObservable<MeshNode> CommentIssue(string spacePath, int number, string body, string userId)
    {
        if (string.IsNullOrWhiteSpace(body))
            return Observable.Throw<MeshNode>(new InvalidOperationException("Enter a comment."));
        return WithRepoAndToken(spacePath, userId, (repoUrl, _, token) =>
            repoClient.CommentIssue(repoUrl, number, body, token)
                // Re-read the full issue (with comments) and upsert — the node stays authoritative.
                .SelectMany(_ => repoClient.GetIssue(repoUrl, number, token))
                .SelectMany(issue => UpsertIssueNode(spacePath, issue)));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Reads (mesh)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The Space's synced issue nodes (ordering is left to the caller — query lists children).</summary>
    public IObservable<IReadOnlyList<MeshNode>> ListIssueNodes(string spacePath) =>
        meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{IssueNamespace(spacePath)} scope:children nodeType:{NodeType}"))
            .Take(1)
            .Select(c => (IReadOnlyList<MeshNode>)c.Items.ToList());

    /// <summary>
    /// LIVE stream of the Space's synced issue nodes — re-emits whenever an issue is synced,
    /// created, or updated (via the shared synced query). The GUI grid binds to this so it
    /// refreshes on its own after a sync or a webhook.
    /// </summary>
    public IObservable<IReadOnlyList<MeshNode>> WatchIssueNodes(string spacePath) =>
        hub.GetWorkspace()
            .GetQuery($"gh-issues:{spacePath}",
                $"path:{IssueNamespace(spacePath)} scope:children nodeType:{NodeType}")
            .Select(nodes => (IReadOnlyList<MeshNode>)(nodes?.ToList() ?? new List<MeshNode>()));

    /// <summary>Live content of one issue node (or null when absent) — the authoritative single-node stream.</summary>
    public IObservable<GitHubIssue?> WatchIssue(string issuePath) =>
        hub.GetWorkspace().GetMeshNodeStream(issuePath).Select(node => Extract<GitHubIssue>(node));

    // ══════════════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create-or-update the <c>{spacePath}/_Issue/{number}</c> node with the issue snapshot.
    /// The number is a stable, immutable handle, so it IS the node id — re-syncing an existing
    /// issue updates the same node in place.
    /// </summary>
    private IObservable<MeshNode> UpsertIssueNode(string spacePath, GitHubIssue issue)
    {
        var node = new MeshNode(issue.Number.ToString(), IssueNamespace(spacePath))
        {
            NodeType = NodeType,
            Name = issue.Title is { Length: > 0 } t ? $"#{issue.Number} {t}" : $"Issue #{issue.Number}",
            State = MeshNodeState.Active,
            MainNode = spacePath,
            Content = issue,
        };
        // The write runs as the calling user: AccessContext is an AsyncLocal that ExecutionContext
        // carries across the IoPool's ConfigureAwait(false) hops, so it is still set here even after
        // the repoClient round-trip. Return the server-reconciled node (version/normalization) on success.
        return hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node))
            .FirstAsync()
            .Select(d => d.Message)
            .SelectMany(resp => resp.Success
                ? Observable.Return(resp.Node ?? node)
                : Observable.Throw<MeshNode>(new InvalidOperationException(
                    $"Failed to sync issue #{issue.Number}: {resp.Error}")));
    }

    /// <summary>Resolves (repo URL, config, token) for the Space + user, then runs the operation.</summary>
    private IObservable<T> WithRepoAndToken<T>(
        string spacePath, string userId, Func<string, GitHubSyncConfig, string, IObservable<T>> op)
    {
        return sync.ReadConfig(spacePath).Take(1).SelectMany(config =>
        {
            if (config?.RepositoryUrl is not { Length: > 0 } repoUrl)
                return Observable.Throw<T>(new InvalidOperationException(
                    "No repository URL is set for this Space. Set one in the GitHub Sync settings, then try again."));
            return credentials.Get(userId).Take(1).SelectMany(cred =>
            {
                if (cred?.AccessToken is not { Length: > 0 } token)
                    return Observable.Throw<T>(new InvalidOperationException(
                        "Connect your GitHub account first (GitHub Sync settings → Connect)."));
                return op(repoUrl, config, token);
            });
        });
    }

    // ContentAs (tolerant): typed → as-is, degraded JsonElement → recovered, otherwise null + LOGGED.
    private T? Extract<T>(MeshNode? node) where T : class
        => node.ContentAs<T>(hub.JsonSerializerOptions, logger);
}
