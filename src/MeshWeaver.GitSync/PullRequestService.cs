using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// GitHub operations layered on top of <see cref="GitHubSyncService"/>: create branch,
/// checkout / update-to-latest, and the full "open pull request" flow (AI-draft → user
/// edits the bound node → submit). The <see cref="GitHubPullRequest"/> satellite node at
/// <c>{spacePath}/_PullRequest/{id}</c> is the data-binding anchor; this service creates it
/// (create-on-absent via <see cref="IMeshService.CreateNode"/>) and, on open, writes back ONLY
/// the immutable GitHub handle (number + url) via <c>GetMeshNodeStream(path).Update(...)</c> —
/// never a bespoke request type.
///
/// <para>🚨 <b>Delegate to GitHub — don't replicate.</b> Every Git operation runs ON GitHub
/// (create branch, commit-on-HEAD, open PR). The drift-prone lifecycle status is read LIVE via
/// <see cref="AskStatus"/> (delegated to GitHub) and never persisted. Content changes coming
/// FROM Git enter the Space only through the import pipeline (see <see cref="UpdateToLatest"/> /
/// <see cref="GitHubSyncService.ReimportAtCommit"/>), never by ad-hoc node edits.</para>
///
/// <para>🚨 Reactive end-to-end (no <c>async</c>/<c>await</c>/<c>Task</c>). Every GitHub leaf is
/// already bridged through <see cref="MeshWeaver.Mesh.Threading.IIoPool"/> inside
/// <see cref="OctokitGitHubRepoClient"/>; this service only composes those observables.</para>
/// </summary>
public sealed class PullRequestService
{
    /// <summary>The <see cref="MeshNode.NodeType"/> of a pull-request draft/handle node.</summary>
    public const string NodeType = "PullRequest";
    /// <summary>The satellite namespace segment under a Space that holds pull-request nodes.</summary>
    public const string SatelliteSegment = "_PullRequest";

    private readonly IMessageHub hub;
    private readonly IMeshService meshService;
    private readonly IGitHubRepoClient repoClient;
    private readonly GitHubCredentialService credentials;
    private readonly GitHubSyncService sync;
    private readonly IPullRequestDraftService draftService;
    private readonly ILogger? logger;

    /// <summary>Initializes a new instance of the <c>PullRequestService</c> class.</summary>
    /// <param name="hub">The message hub used for workspace access and node updates.</param>
    /// <param name="meshService">Mesh service used to create and query pull-request nodes.</param>
    /// <param name="repoClient">The GitHub repo client performing branch/PR operations on GitHub.</param>
    /// <param name="credentials">Per-user GitHub credential store providing the OAuth access token.</param>
    /// <param name="sync">The sync service providing repo config and the import/mirror pipeline.</param>
    /// <param name="draftService">The AI-backed service that drafts the PR title and body.</param>
    /// <param name="logger">Optional logger.</param>
    public PullRequestService(
        IMessageHub hub,
        IMeshService meshService,
        IGitHubRepoClient repoClient,
        GitHubCredentialService credentials,
        GitHubSyncService sync,
        IPullRequestDraftService draftService,
        ILogger<PullRequestService>? logger = null)
    {
        this.hub = hub;
        this.meshService = meshService;
        this.repoClient = repoClient;
        this.credentials = credentials;
        this.sync = sync;
        this.draftService = draftService;
        this.logger = logger;
    }

    /// <summary>The satellite namespace under a Space that holds PR nodes.</summary>
    public static string PullRequestNamespace(string spacePath) => $"{spacePath}/{SatelliteSegment}";

    /// <summary>The node path for a PR with the given id.</summary>
    public static string PullRequestPath(string spacePath, string id) => $"{PullRequestNamespace(spacePath)}/{id}";

    // ══════════════════════════════════════════════════════════════════════════
    //  Branch + checkout / update-to-latest
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates <paramref name="newBranch"/> in the Space's configured repo from
    /// <paramref name="baseRef"/> (a branch name or commit SHA), authenticated as
    /// <paramref name="userId"/>. Emits the created branch + the SHA it points at.
    /// </summary>
    public IObservable<GitHubBranchResult> CreateBranch(
        string spacePath, string newBranch, string baseRef, string userId)
    {
        return WithRepoAndToken(spacePath, userId, (repoUrl, _, token) =>
            repoClient.CreateBranch(new GitHubCreateBranchRequest
            {
                RepositoryUrl = repoUrl,
                NewBranch = newBranch,
                BaseRef = string.IsNullOrWhiteSpace(baseRef) ? "main" : baseRef,
                AccessToken = token,
            }));
    }

    /// <summary>
    /// "Update to latest" — re-imports the Space at its configured branch HEAD (delegating
    /// to <see cref="GitHubSyncService.ReimportAtCommit"/>, which fetches + mirrors). This is
    /// the checkout operation: the working Space is brought to the latest repo state.
    /// </summary>
    public IObservable<StaticRepoImportResult> UpdateToLatest(string spacePath, string userId)
    {
        return sync.ReadConfig(spacePath).Take(1).SelectMany(config =>
        {
            if (config?.RepositoryUrl is not { Length: > 0 })
                return Observable.Throw<StaticRepoImportResult>(new InvalidOperationException(
                    "No GitHub repository configured for this Space."));
            var branch = string.IsNullOrWhiteSpace(config.Branch) ? "main" : config.Branch;
            logger?.LogInformation("Updating {Space} to latest on {Branch}", spacePath, branch);
            return sync.ReimportAtCommit(spacePath, branch, userId);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Open PR — AI draft → user edits the bound node → submit
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Step 1+2 of the open-PR flow: ask the AI to draft a title + body from the change
    /// context, then create (create-on-absent) a draft <see cref="GitHubPullRequest"/> node at
    /// <c>{spacePath}/_PullRequest/{id}</c> with <see cref="PullRequestStatus.Draft"/>. The GUI
    /// then binds the standard node-content editor to this node for the user's edits. Emits the
    /// created node.
    ///
    /// <para>The head branch defaults to the Space's configured sync branch (where "Sync now"
    /// committed); the base defaults to <c>main</c> (or the configured branch when it is not
    /// the head).</para>
    /// </summary>
    public IObservable<MeshNode> CreateDraft(string spacePath, string? headBranch, string baseBranch)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        return ReadSpace(spacePath).SelectMany(space =>
            sync.ReadConfig(spacePath).Take(1).SelectMany(config =>
            {
                var head = !string.IsNullOrWhiteSpace(headBranch) ? headBranch!
                    : !string.IsNullOrWhiteSpace(config?.Branch) ? config!.Branch : "main";
                var @base = string.IsNullOrWhiteSpace(baseBranch) ? "main" : baseBranch.Trim();
                var (name, summary) = SpaceContext(space, spacePath);

                return draftService.DraftAsync(name, summary, head, @base)
                    // If the AI is unavailable (no model configured), fall back to a sensible
                    // placeholder draft so the user can still edit + submit — surfaced, not silenced.
                    .Catch<PullRequestDraft, Exception>(ex =>
                    {
                        logger?.LogInformation(ex,
                            "PR draft AI unavailable for {Space}; using placeholder draft.", spacePath);
                        return Observable.Return(new PullRequestDraft(
                            $"Sync {name} ({head} → {@base})",
                            $"Mirrors the **{name}** Space from MeshWeaver into `{head}`.\n\n" +
                            "_(AI draft unavailable — edit this body before submitting.)_"));
                    })
                    .SelectMany(draft =>
                    {
                        // Local draft state only — no Number/Url yet (not opened), no status
                        // (status is asked live from GitHub once opened).
                        var content = new GitHubPullRequest
                        {
                            Title = draft.Title,
                            Body = draft.Body,
                            HeadBranch = head,
                            BaseBranch = @base,
                        };
                        var node = new MeshNode(id, PullRequestNamespace(spacePath))
                        {
                            NodeType = NodeType,
                            Name = draft.Title,
                            State = MeshNodeState.Active,
                            MainNode = spacePath,
                            Content = content,
                        };
                        logger?.LogInformation("Creating draft PR {Path} ({Head} → {Base})",
                            node.Path, head, @base);
                        return meshService.CreateNode(node);
                    });
            }));
    }

    /// <summary>
    /// Step 4 of the open-PR flow: reads the (user-edited) PR node, opens the PR on GitHub
    /// <c>head → base</c>, and writes back ONLY the immutable GitHub handle
    /// (<see cref="GitHubPullRequest.Number"/> / <see cref="GitHubPullRequest.Url"/>) via
    /// <c>stream.Update</c> (read-modify-write — only the handle changes, so a concurrent
    /// Title/Body edit is never clobbered). The lifecycle <b>status is NOT stored</b> — it is
    /// asked live from GitHub via <see cref="AskStatus"/>. Emits the opened-PR info.
    /// </summary>
    public IObservable<GitHubPullRequestInfo> Submit(string spacePath, string prPath, string userId)
    {
        return ReadPullRequest(prPath).SelectMany(pr =>
        {
            if (pr is null)
                return Observable.Throw<GitHubPullRequestInfo>(new InvalidOperationException(
                    "Pull-request draft not found."));
            if (string.IsNullOrWhiteSpace(pr.Title))
                return Observable.Throw<GitHubPullRequestInfo>(new InvalidOperationException(
                    "Enter a title before submitting the pull request."));
            if (string.IsNullOrWhiteSpace(pr.HeadBranch))
                return Observable.Throw<GitHubPullRequestInfo>(new InvalidOperationException(
                    "The pull request has no head branch."));

            return WithRepoAndToken(spacePath, userId, (repoUrl, _, token) =>
                repoClient.OpenPullRequest(new GitHubOpenPullRequestRequest
                {
                    RepositoryUrl = repoUrl,
                    Title = pr.Title!,
                    Body = pr.Body,
                    HeadBranch = pr.HeadBranch!,
                    BaseBranch = string.IsNullOrWhiteSpace(pr.BaseBranch) ? "main" : pr.BaseBranch,
                    AccessToken = token,
                })
                .SelectMany(info => WriteHandle(prPath, info).Select(_ => info)));
        });
    }

    /// <summary>
    /// Asks GitHub for the PR's CURRENT lifecycle state (open / merged / closed) — delegated,
    /// LIVE, never persisted. The GUI binds to this for the status badge so it can never drift
    /// from GitHub. Emits <see cref="PullRequestStatus.Draft"/> (with the local draft's identity)
    /// when the PR has not been opened yet, otherwise the live GitHub state.
    /// </summary>
    public IObservable<GitHubPullRequestInfo> AskStatus(string spacePath, string prPath, string userId)
    {
        return ReadPullRequest(prPath).SelectMany(pr =>
        {
            if (pr?.Number is not { } number)
                // Not opened yet — the only state we own is "Draft"; no GitHub call.
                return Observable.Return(new GitHubPullRequestInfo(0, pr?.Url ?? "", PullRequestStatus.Draft));

            return WithRepoAndToken(spacePath, userId, (repoUrl, _, token) =>
                repoClient.GetPullRequestStatus(repoUrl, number, token));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Reads / writes
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Live PR node content (or null when absent) — the authoritative single-node stream.</summary>
    public IObservable<GitHubPullRequest?> WatchPullRequest(string prPath) =>
        hub.GetWorkspace().GetMeshNodeStream(prPath).Select(node => Extract<GitHubPullRequest>(node));

    private IObservable<GitHubPullRequest?> ReadPullRequest(string prPath) =>
        WatchPullRequest(prPath).Take(1).Timeout(TimeSpan.FromSeconds(30));

    /// <summary>The Space's PRs (most recent first is left to the caller — query lists children).</summary>
    public IObservable<IReadOnlyList<MeshNode>> ListPullRequests(string spacePath) =>
        meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{PullRequestNamespace(spacePath)} scope:children nodeType:{NodeType}"))
            .Take(1)
            .Select(c => (IReadOnlyList<MeshNode>)c.Items.ToList());

    /// <summary>
    /// Writes ONLY the immutable GitHub handle (number + url) onto the PR node — never the
    /// drift-prone status. Merges atop the latest content so a concurrent Title/Body edit survives.
    /// </summary>
    private IObservable<MeshNode> WriteHandle(string prPath, GitHubPullRequestInfo info)
    {
        return hub.GetWorkspace().GetMeshNodeStream(prPath).Update(node =>
        {
            var cur = Extract<GitHubPullRequest>(node) ?? new GitHubPullRequest();
            return node with { Content = cur with { Number = info.Number, Url = info.Url } };
        });
    }

    private IObservable<MeshNode?> ReadSpace(string spacePath) =>
        hub.GetWorkspace().GetMeshNodeStream(spacePath)
            .Where(n => n is not null).Take(1).Timeout(TimeSpan.FromSeconds(30))
            .Select(n => (MeshNode?)n);

    private (string Name, string? Summary) SpaceContext(MeshNode? space, string spacePath)
    {
        var name = space?.Name ?? spacePath;
        var summary = space?.Content switch
        {
            Space s => !string.IsNullOrWhiteSpace(s.Description) ? s.Description
                : !string.IsNullOrWhiteSpace(s.Body) ? s.Body : null,
            MarkdownContent mc => mc.Content,
            _ => null,
        };
        return (name, summary);
    }

    /// <summary>Resolves (repo URL, config, token) for the Space + user, then runs the operation.</summary>
    private IObservable<T> WithRepoAndToken<T>(
        string spacePath, string userId, Func<string, GitHubSyncConfig, string, IObservable<T>> op)
    {
        return sync.ReadConfig(spacePath).Take(1).SelectMany(config =>
        {
            if (config?.RepositoryUrl is not { Length: > 0 } repoUrl)
                return Observable.Throw<T>(new InvalidOperationException(
                    "No repository URL is set for this Space. Set one in the Repository section, then try again."));
            return credentials.Get(userId).Take(1).SelectMany(cred =>
            {
                if (cred?.AccessToken is not { Length: > 0 } token)
                    return Observable.Throw<T>(new InvalidOperationException(
                        "Connect your GitHub account first (GitHub Sync settings → Connect)."));
                return op(repoUrl, config, token);
            });
        });
    }

    private T? Extract<T>(MeshNode? node) where T : class
    {
        if (node?.Content is null) return null;
        if (node.Content is T typed) return typed;
        if (node.Content is System.Text.Json.JsonElement je)
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<T>(je.GetRawText(), hub.JsonSerializerOptions); }
            catch { return null; }
        }
        return null;
    }
}
