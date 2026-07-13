using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Octokit;
using Octokit.Reactive;

namespace MeshWeaver.GitSync;

/// <summary>
/// Production <see cref="IGitHubRepoClient"/> over the Octokit.Reactive Git Data API. Every
/// GitHub leaf is a native <see cref="IObservable{T}"/> from
/// <see cref="IObservableGitHubClient"/> bridged through the <see cref="IoPoolNames.Http"/>
/// pool (<c>Http.InvokeObservable(ct =&gt; client.X(…))</c>) and composed reactively — no
/// <c>async</c>/<c>await</c>/<c>Task</c> escapes a method signature, per
/// <c>Doc/Architecture/ControlledIoPooling.md</c>. The reactive client's observables are
/// themselves <c>FromAsync</c>-shaped, so routing every one through
/// <see cref="IIoPool.InvokeObservable{T}"/> keeps them concurrency-bounded and off the hub
/// scheduler (they would otherwise deadlock a hub/grain turn).
///
/// <para>Export uses a single commit: blob per file → one tree → commit → update
/// ref. Mirror is achieved by reconstructing the tree from scratch (no base_tree):
/// within the configured subdirectory the tree contains exactly the exported files
/// (so removed files vanish); blobs outside the subdirectory are carried over by
/// their existing sha, so the rest of the repo is untouched.</para>
/// </summary>
public sealed class OctokitGitHubRepoClient(IoPoolRegistry ioPools, ILogger<OctokitGitHubRepoClient>? logger = null)
    : IGitHubRepoClient
{
    private const string BlobMode = "100644";
    private static readonly ProductHeaderValue Product = new("MeshWeaver");

    private IIoPool Http => ioPools.Get(IoPoolNames.Http);

    // An empty token means ANONYMOUS access (a public repo, e.g. the plugin registry serving a
    // public plugins repo with no App identity configured). Octokit's `new Credentials("")` THROWS
    // ArgumentException "String cannot be empty (Parameter 'token')", so never hand it an empty
    // string — build a credential-less client instead (unauthenticated, lower rate limit but valid).
    private static IObservableGitHubClient Client(string token) =>
        new ObservableGitHubClient(string.IsNullOrEmpty(token)
            ? new GitHubClient(Product)
            : new GitHubClient(Product) { Credentials = new Credentials(token) });

    /// <summary>Parses <c>https://github.com/owner/repo(.git)</c> into (owner, repo).</summary>
    public static (string Owner, string Repo) ParseRepoUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Repository URL is required.", nameof(url));
        var trimmed = url.Trim();
        // Accept owner/repo shorthand too.
        if (!trimmed.Contains("://") && trimmed.Count(c => c == '/') == 1)
        {
            var p = trimmed.Split('/');
            return (p[0], StripGit(p[1]));
        }
        var uri = new Uri(trimmed, UriKind.Absolute);
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            throw new ArgumentException($"Cannot parse owner/repo from '{url}'.", nameof(url));
        return (segments[0], StripGit(segments[1]));
    }

    private static string StripGit(string repo) =>
        repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo;

    /// <summary>
    /// Mirrors the request's files into the repo as a single commit (blob → tree → commit →
    /// update ref), creating the repo and/or branch when missing per the request flags.
    /// </summary>
    /// <param name="request">The push/mirror request (repo, branch, files, author, token, create flags).</param>
    /// <returns>An observable emitting the push outcome (commit SHA, files written/deleted, repo-created flag).</returns>
    public IObservable<GitHubPushResult> Push(GitHubPushRequest request)
    {
        var (owner, repo) = ParseRepoUrl(request.RepositoryUrl);
        var client = Client(request.AccessToken);
        var prefix = NormalizePrefix(request.Subdirectory);
        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch;

        return EnsureRepo(client, owner, repo, request.CreatePrivateIfMissing)
            .SelectMany(repoCreated => ReadHead(client, owner, repo, branch)
                .SelectMany(head0 =>
                {
                    if (!head0.RefExists && !request.CreateBranchIfMissing)
                        return Observable.Throw<GitHubPushResult>(new InvalidOperationException(
                            $"Branch '{branch}' does not exist and branch auto-create is disabled."));

                    // A brand-new EMPTY repo (zero commits) rejects the Git Data API: blob/tree/commit
                    // creation returns 409 "Git Repository is empty." until a first commit exists, and
                    // ONLY the Contents API can create that. EnsureInitialCommit seeds a throwaway file
                    // on the branch to initialize an empty repo, then we mirror normally — the mirror
                    // reconstructs the tree (dropping the seed) so the real content lands as the commit.
                    return EnsureInitialCommit(client, owner, repo, branch, prefix, head0)
                    .SelectMany(head =>
                    {
                    // Existing blob entries (full repo). Empty when the repo/branch has no commit yet.
                    var existing = head.ExistingBlobs;
                    // Within the mirrored prefix: these are the candidates that may be deleted.
                    var underPrefix = existing
                        .Where(e => prefix.Length == 0 || e.Path.StartsWith(prefix, StringComparison.Ordinal))
                        .Select(e => e.Path)
                        .ToHashSet(StringComparer.Ordinal);
                    // Preserved entries: everything OUTSIDE the prefix (untouched). When prefix is empty
                    // (whole-repo mirror) nothing is preserved — the export fully defines the tree.
                    var preserved = prefix.Length == 0
                        ? Array.Empty<(string Path, string Sha)>()
                        : existing.Where(e => !e.Path.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

                    var exportPaths = request.Files.Select(f => prefix + f.Path).ToHashSet(StringComparer.Ordinal);
                    var deleted = underPrefix.Count(p => !exportPaths.Contains(p));

                    return CreateBlobs(client, owner, repo, request.Files, prefix)
                        .SelectMany(exportEntries =>
                        {
                            var newTree = new NewTree();
                            foreach (var (path, sha) in preserved)
                                newTree.Tree.Add(new NewTreeItem { Path = path, Mode = BlobMode, Type = TreeType.Blob, Sha = sha });
                            foreach (var (path, sha) in exportEntries)
                                newTree.Tree.Add(new NewTreeItem { Path = path, Mode = BlobMode, Type = TreeType.Blob, Sha = sha });

                            return Http.InvokeObservable(ct => client.Git.Tree.Create(owner, repo, newTree))
                                .SelectMany(tree =>
                                {
                                    var commit = head.CommitSha is { Length: > 0 } parent
                                        ? new NewCommit(request.CommitMessage, tree.Sha, parent)
                                        : new NewCommit(request.CommitMessage, tree.Sha, Array.Empty<string>());
                                    var who = new Committer(request.AuthorName, request.AuthorEmail, DateTimeOffset.UtcNow);
                                    commit.Author = who;
                                    commit.Committer = who;
                                    return Http.InvokeObservable(ct => client.Git.Commit.Create(owner, repo, commit));
                                })
                                .SelectMany(commit => UpdateRef(client, owner, repo, branch, commit.Sha, head.RefExists)
                                    .Select(_ => new GitHubPushResult(
                                        commit.Sha, request.RepositoryUrl,
                                        request.Files.Count, deleted, repoCreated)));
                        });
                    });
                }));
    }

    /// <summary>
    /// Reads a repo snapshot at a commitish: resolves the commit, lists its blobs (optionally
    /// confined to <paramref name="subdirectory"/>) and downloads their decoded content.
    /// </summary>
    /// <param name="repositoryUrl">The repository URL to read from.</param>
    /// <param name="commitish">A branch name or commit SHA to snapshot.</param>
    /// <param name="subdirectory">The repo subdirectory to confine the snapshot to; null/empty reads the whole repo.</param>
    /// <param name="accessToken">The user's OAuth access token.</param>
    /// <returns>An observable emitting the resolved commit SHA and its files (paths made subdirectory-relative).</returns>
    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        => Fetch(repositoryUrl, commitish, subdirectory, accessToken, _ => true);

    /// <summary>
    /// Filtered fetch — the tree is filtered BEFORE the per-blob reads, so fetching every package's
    /// <c>*/index.json</c> from a large repo costs the ref + tree lookups plus one blob read per
    /// manifest, not one per file in the repo.
    /// </summary>
    /// <param name="repositoryUrl">The repository URL (https://github.com/owner/repo).</param>
    /// <param name="commitish">Branch name or commit SHA to read at.</param>
    /// <param name="subdirectory">Optional subtree; returned paths are relative to it.</param>
    /// <param name="accessToken">The token to read with (empty for anonymous/public).</param>
    /// <param name="pathFilter">Predicate over the subdirectory-relative path; only matches download.</param>
    /// <returns>The snapshot at the resolved commit containing only the matching files.</returns>
    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken,
        Func<string, bool> pathFilter)
    {
        ArgumentNullException.ThrowIfNull(pathFilter);
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var client = Client(accessToken);
        var prefix = NormalizePrefix(subdirectory);
        var commitRef = string.IsNullOrWhiteSpace(commitish) ? "main" : commitish.Trim();

        return ResolveCommitish(client, owner, repo, commitRef)
            .SelectMany(head =>
            {
                var blobs = head.ExistingBlobs
                    .Where(e => prefix.Length == 0 || e.Path.StartsWith(prefix, StringComparison.Ordinal))
                    .Where(e => pathFilter(prefix.Length == 0 ? e.Path : e.Path[prefix.Length..]))
                    .ToArray();
                if (blobs.Length == 0)
                    return Observable.Return(new RepoSnapshot(head.CommitSha!, Array.Empty<RepoFile>()));

                return blobs
                    .Select(e => Http.InvokeObservable(ct => client.Git.Blob.Get(owner, repo, e.Sha))
                        .Select(blob => new RepoFile(
                            prefix.Length == 0 ? e.Path : e.Path[prefix.Length..],
                            DecodeBlob(blob))))
                    .Merge(8)
                    .ToList()
                    .Select(list => new RepoSnapshot(head.CommitSha!, (IReadOnlyList<RepoFile>)list));
            });
    }

    /// <summary>
    /// The commit SHA <paramref name="commitish"/> resolves to — one ref (or commit) lookup, no
    /// tree, no blobs. The cheap change counter for pollers: compare against the last processed
    /// SHA and skip the fetch entirely when nothing was merged.
    /// </summary>
    /// <param name="repositoryUrl">The repository URL (https://github.com/owner/repo).</param>
    /// <param name="commitish">Branch name or commit SHA.</param>
    /// <param name="accessToken">The token to read with (empty for anonymous/public).</param>
    /// <returns>An observable emitting the resolved commit SHA.</returns>
    public IObservable<string> GetHeadSha(string repositoryUrl, string commitish, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var client = Client(accessToken);
        var commitRef = string.IsNullOrWhiteSpace(commitish) ? "main" : commitish.Trim();
        return ResolveRefSha(client, owner, repo, commitRef);
    }

    /// <summary>Creates a branch from an existing ref (branch name or SHA) resolved to its commit.</summary>
    /// <param name="request">The create-branch request (repo, new branch, base ref, token).</param>
    /// <returns>An observable emitting the new branch name and the commit SHA it points at.</returns>
    public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request)
    {
        var (owner, repo) = ParseRepoUrl(request.RepositoryUrl);
        var client = Client(request.AccessToken);
        var baseRef = string.IsNullOrWhiteSpace(request.BaseRef) ? "main" : request.BaseRef.Trim();
        var newBranch = request.NewBranch.Trim();
        if (string.IsNullOrEmpty(newBranch))
            return Observable.Throw<GitHubBranchResult>(new ArgumentException("New branch name is required.", nameof(request)));

        return ResolveRefSha(client, owner, repo, baseRef)
            .SelectMany(sha =>
            {
                logger?.LogInformation("Creating branch {Branch} from {BaseRef} ({Sha}) in {Owner}/{Repo}.",
                    newBranch, baseRef, sha[..Math.Min(8, sha.Length)], owner, repo);
                return Http.InvokeObservable(ct => client.Git.Reference.Create(
                        owner, repo, new NewReference($"refs/heads/{newBranch}", sha)))
                    .Select(reference => new GitHubBranchResult(newBranch, reference.Object.Sha));
            });
    }

    /// <summary>Opens a pull request <c>head → base</c> on GitHub.</summary>
    /// <param name="request">The open-PR request (repo, title, body, head/base branches, token).</param>
    /// <returns>An observable emitting the opened pull request's number, URL and state.</returns>
    public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request)
    {
        var (owner, repo) = ParseRepoUrl(request.RepositoryUrl);
        var client = Client(request.AccessToken);
        var baseBranch = string.IsNullOrWhiteSpace(request.BaseBranch) ? "main" : request.BaseBranch.Trim();
        var head = request.HeadBranch?.Trim();
        if (string.IsNullOrEmpty(head))
            return Observable.Throw<GitHubPullRequestInfo>(new ArgumentException("Head branch is required.", nameof(request)));

        var newPr = new NewPullRequest(request.Title, head, baseBranch) { Body = request.Body ?? "" };
        logger?.LogInformation("Opening PR {Head} → {Base} in {Owner}/{Repo}.", head, baseBranch, owner, repo);
        return Http.InvokeObservable(ct => client.PullRequest.Create(owner, repo, newPr))
            .Select(ToInfo);
    }

    /// <summary>Reads a pull request's current live state (open / closed / merged) from GitHub.</summary>
    /// <param name="repositoryUrl">The repository URL the pull request belongs to.</param>
    /// <param name="number">The pull request number.</param>
    /// <param name="accessToken">The user's OAuth access token.</param>
    /// <returns>An observable emitting the pull request's number, URL and current merged state.</returns>
    public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
        string repositoryUrl, int number, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var client = Client(accessToken);
        return Http.InvokeObservable(ct => client.PullRequest.Get(owner, repo, number))
            .Select(ToInfo);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Issues
    // ══════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
        string repositoryUrl, GitHubIssueState? state, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var client = Client(accessToken);
        var request = new RepositoryIssueRequest { State = MapStateFilter(state) };
        // GetAllForRepository streams one Issue per page-item AND includes pull requests (a PR is an
        // issue on GitHub); drop those, map, and .ToList() so the pool-bridged leaf emits one list.
        return Http.InvokeObservable(ct => client.Issue.GetAllForRepository(owner, repo, request)
                .Where(i => i.PullRequest is null)
                .Select(ToIssue)
                .ToList())
            .Select(list => (IReadOnlyList<GitHubIssue>)list);
    }

    /// <inheritdoc />
    public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var client = Client(accessToken);
        return Http.InvokeObservable(ct => client.Issue.Get(owner, repo, number))
            .SelectMany(issue => Http.InvokeObservable(ct =>
                    client.Issue.Comment.GetAllForIssue(owner, repo, number).ToList())
                .Select(comments => ToIssue(issue) with
                {
                    Comments = comments.Select(ToComment).ToImmutableList(),
                }));
    }

    /// <inheritdoc />
    public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request)
    {
        var (owner, repo) = ParseRepoUrl(request.RepositoryUrl);
        var client = Client(request.AccessToken);
        var newIssue = new NewIssue(request.Title) { Body = request.Body ?? "" };
        foreach (var label in request.Labels)
            newIssue.Labels.Add(label);
        logger?.LogInformation("Creating issue '{Title}' in {Owner}/{Repo}.", request.Title, owner, repo);
        return Http.InvokeObservable(ct => client.Issue.Create(owner, repo, newIssue))
            .Select(ToIssue);
    }

    /// <inheritdoc />
    public IObservable<GitHubIssueComment> CommentIssue(
        string repositoryUrl, int number, string body, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var client = Client(accessToken);
        return Http.InvokeObservable(ct => client.Issue.Comment.Create(owner, repo, number, body))
            .Select(ToComment);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Pull requests (richer: list / detail / comment / merge)
    // ══════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
        string repositoryUrl, PullRequestStatus? state, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var client = Client(accessToken);
        var request = new PullRequestRequest { State = MapPrStateFilter(state) };
        return Http.InvokeObservable(ct => client.PullRequest.GetAllForRepository(owner, repo, request)
                .Select(ToSummary)
                .ToList())
            .Select(list => (IReadOnlyList<GitHubPullRequestSummary>)list);
    }

    /// <inheritdoc />
    public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
        string repositoryUrl, int number, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var client = Client(accessToken);
        return Http.InvokeObservable(ct => client.PullRequest.Get(owner, repo, number))
            .SelectMany(pr =>
            {
                var headSha = pr.Head?.Sha;
                var reviews = Http.InvokeObservable(ct =>
                        client.PullRequest.Review.GetAll(owner, repo, number).ToList())
                    .Select(SummarizeReviews);
                // Checks live over the head commit; a repo with no checks / a token lacking the
                // checks scope returns empty rather than failing the whole detail read.
                var checks = string.IsNullOrEmpty(headSha)
                    ? Observable.Return(GitHubCheckSummary.Empty)
                    : Http.InvokeObservable(ct => client.Check.Run.GetAllForReference(owner, repo, headSha))
                        .Select(SummarizeChecks)
                        // Only "no checks configured / no checks:read scope" (404 / 403) degrade to empty;
                        // a real error (auth, 5xx) must surface, not be silently hidden as "no checks".
                        .Catch<GitHubCheckSummary, ApiException>(ex =>
                            ex is NotFoundException || ex.StatusCode == System.Net.HttpStatusCode.Forbidden
                                ? Observable.Return(GitHubCheckSummary.Empty)
                                : Observable.Throw<GitHubCheckSummary>(ex));
                // Both leaves emit exactly once → Zip pairs them into a single detail emission.
                return reviews.Zip(checks, (rev, chk) => ToDetail(pr, headSha, chk, rev));
            });
    }

    /// <inheritdoc />
    public IObservable<GitHubIssueComment> CommentPullRequest(
        string repositoryUrl, int number, string body, string accessToken)
        // A pull request IS an issue on GitHub — PR conversation comments go through the issues API.
        => CommentIssue(repositoryUrl, number, body, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request)
    {
        var (owner, repo) = ParseRepoUrl(request.RepositoryUrl);
        var client = Client(request.AccessToken);
        var merge = new MergePullRequest
        {
            CommitTitle = request.CommitTitle,
            CommitMessage = request.CommitMessage,
            MergeMethod = request.Method switch
            {
                GitHubMergeMethod.Squash => PullRequestMergeMethod.Squash,
                GitHubMergeMethod.Rebase => PullRequestMergeMethod.Rebase,
                _ => PullRequestMergeMethod.Merge,
            },
        };
        logger?.LogInformation("Merging PR #{Number} in {Owner}/{Repo} ({Method}).",
            request.Number, owner, repo, request.Method);
        return Http.InvokeObservable(ct => client.PullRequest.Merge(owner, repo, request.Number, merge))
            .Select(m => new GitHubMergeResult(m.Merged, m.Sha, m.Message));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Maps an Octokit <see cref="PullRequest"/> to our merged-state info record.</summary>
    private static GitHubPullRequestInfo ToInfo(PullRequest pr) =>
        new(pr.Number, pr.HtmlUrl, MapStatus(pr));

    /// <summary>GitHub <c>state</c> + <c>merged</c> → <see cref="PullRequestStatus"/>.</summary>
    private static PullRequestStatus MapStatus(PullRequest pr) =>
        pr.Merged ? PullRequestStatus.Merged
        : pr.State.Value == ItemState.Closed ? PullRequestStatus.Closed
        : PullRequestStatus.Open;

    /// <summary>Our issue-state filter → Octokit's <see cref="ItemStateFilter"/> (null = all).</summary>
    private static ItemStateFilter MapStateFilter(GitHubIssueState? state) => state switch
    {
        GitHubIssueState.Open => ItemStateFilter.Open,
        GitHubIssueState.Closed => ItemStateFilter.Closed,
        _ => ItemStateFilter.All,
    };

    /// <summary>Our PR-status filter → Octokit's <see cref="ItemStateFilter"/> (Merged folds into Closed).</summary>
    private static ItemStateFilter MapPrStateFilter(PullRequestStatus? state) => state switch
    {
        PullRequestStatus.Open => ItemStateFilter.Open,
        PullRequestStatus.Closed or PullRequestStatus.Merged => ItemStateFilter.Closed,
        _ => ItemStateFilter.All,
    };

    /// <summary>Maps an Octokit <see cref="Issue"/> to our snapshot record (comments filled separately).</summary>
    private static GitHubIssue ToIssue(Issue issue) => new()
    {
        Number = issue.Number,
        Title = issue.Title,
        Body = issue.Body,
        State = issue.State.Value == ItemState.Closed ? GitHubIssueState.Closed : GitHubIssueState.Open,
        AuthorLogin = issue.User?.Login,
        Labels = issue.Labels.Select(l => l.Name).ToImmutableList(),
        Assignees = issue.Assignees.Select(a => a.Login).ToImmutableList(),
        CommentsCount = issue.Comments,
        Url = issue.HtmlUrl,
        CreatedAt = issue.CreatedAt,
        UpdatedAt = issue.UpdatedAt,
        ClosedAt = issue.ClosedAt,
    };

    /// <summary>Maps an Octokit <see cref="IssueComment"/> to our comment record.</summary>
    private static GitHubIssueComment ToComment(IssueComment c) =>
        new(c.Id, c.User?.Login, c.Body, c.CreatedAt, c.HtmlUrl);

    /// <summary>Maps an Octokit <see cref="PullRequest"/> to our list-row summary.</summary>
    private static GitHubPullRequestSummary ToSummary(PullRequest pr) =>
        new(pr.Number, pr.Title, pr.User?.Login, MapStatus(pr), pr.Draft,
            pr.Head?.Ref, pr.Base?.Ref, pr.HtmlUrl, pr.CreatedAt, pr.UpdatedAt);

    /// <summary>Combines the PR + its check/review roll-ups into the live detail record.</summary>
    private static GitHubPullRequestDetail ToDetail(
        PullRequest pr, string? headSha, GitHubCheckSummary checks, GitHubReviewSummary reviews) =>
        new(pr.Number, pr.Title, pr.Body, pr.User?.Login, MapStatus(pr), pr.Draft,
            pr.Head?.Ref, pr.Base?.Ref, headSha, pr.HtmlUrl,
            pr.Mergeable, pr.MergeableState?.StringValue, pr.Comments, checks, reviews);

    /// <summary>Rolls PR reviews up to the latest decision per reviewer (dismissed / pending don't count).</summary>
    private static GitHubReviewSummary SummarizeReviews(IList<PullRequestReview> reviews)
    {
        int approved = 0, changes = 0, commented = 0;
        var latestPerReviewer = reviews
            .Where(r => r.User?.Login is { Length: > 0 })
            .GroupBy(r => r.User!.Login, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.SubmittedAt).Last());
        foreach (var r in latestPerReviewer)
        {
            var s = r.State.Value;
            if (s == PullRequestReviewState.Approved) approved++;
            else if (s == PullRequestReviewState.ChangesRequested) changes++;
            else if (s == PullRequestReviewState.Commented) commented++;
        }
        return new GitHubReviewSummary(approved, changes, commented);
    }

    /// <summary>Rolls the head commit's check runs up to totals + an overall state.</summary>
    private static GitHubCheckSummary SummarizeChecks(CheckRunsResponse resp)
    {
        int passed = 0, failed = 0, pending = 0;
        foreach (var run in resp.CheckRuns)
        {
            if (run.Status.Value != CheckStatus.Completed) { pending++; continue; }
            var c = run.Conclusion?.Value;
            if (c is CheckConclusion.Success or CheckConclusion.Neutral or CheckConclusion.Skipped) passed++;
            else failed++;
        }
        var total = passed + failed + pending;
        var overall = failed > 0 ? GitHubCheckState.Failed
            : pending > 0 ? GitHubCheckState.Pending
            : total > 0 ? GitHubCheckState.Passed
            : GitHubCheckState.None;
        return new GitHubCheckSummary(total, passed, failed, pending, overall);
    }

    /// <summary>Resolves a commitish (branch name OR SHA) to its commit SHA — cheap, no tree read.</summary>
    private IObservable<string> ResolveRefSha(IObservableGitHubClient client, string owner, string repo, string commitish)
        => IsSha(commitish)
            ? Http.InvokeObservable(ct => client.Git.Commit.Get(owner, repo, commitish)).Select(c => c.Sha)
            : Http.InvokeObservable(ct => client.Git.Reference.Get(owner, repo, $"heads/{commitish}"))
                .Select(reference => reference.Object.Sha);

    internal sealed record HeadInfo(string? CommitSha, bool RefExists, IReadOnlyList<(string Path, string Sha)> ExistingBlobs);

    /// <summary>
    /// The missing-branch base policy: a new branch on a NON-empty repo builds on the DEFAULT
    /// branch head — its <see cref="HeadInfo.CommitSha"/> becomes the commit's parent and its
    /// <see cref="HeadInfo.ExistingBlobs"/> the preserved tree — while <c>RefExists</c> is forced
    /// false so the push CREATES the target ref. Without this the commit was parent-less: an
    /// ORPHAN branch with no common ancestor, impossible to PR/merge into the default branch.
    /// </summary>
    internal static HeadInfo AsNewBranchBase(HeadInfo defaultHead) => defaultHead with { RefExists = false };

    /// <summary>Ensures the repo exists; creates it private (under the user or the org) when missing.</summary>
    private IObservable<bool> EnsureRepo(IObservableGitHubClient client, string owner, string repo, bool createIfMissing)
        => Http.InvokeObservable(ct => client.Repository.Get(owner, repo))
            .Select(_ => false)
            .Catch<bool, NotFoundException>(_ =>
            {
                if (!createIfMissing)
                    return Observable.Throw<bool>(new InvalidOperationException(
                        $"Repository {owner}/{repo} does not exist and auto-create is disabled."));
                var newRepo = new NewRepository(repo) { Private = true, AutoInit = false };
                logger?.LogInformation("Creating private GitHub repo {Owner}/{Repo}.", owner, repo);
                return Http.InvokeObservable(ct => client.User.Current())
                    .SelectMany(me => string.Equals(me.Login, owner, StringComparison.OrdinalIgnoreCase)
                        ? Http.InvokeObservable(ct => client.Repository.Create(newRepo))
                        : Http.InvokeObservable(ct => client.Repository.Create(owner, newRepo)))
                    .Select(_ => true);
            });

    /// <summary>Reads the branch head commit + its recursive blob list. Tolerates an empty repo (no ref yet).</summary>
    private IObservable<HeadInfo> ReadHead(IObservableGitHubClient client, string owner, string repo, string branch)
        => Http.InvokeObservable(ct => client.Git.Reference.Get(owner, repo, $"heads/{branch}"))
            .SelectMany(reference => Http.InvokeObservable(ct => client.Git.Commit.Get(owner, repo, reference.Object.Sha))
                .SelectMany(commit => TreeOf(client, owner, repo, commit)))
            // No commit to build on → no head, no existing blobs (the export then makes a first,
            // PARENT-LESS commit that creates the branch ref). GitHub signals "no commit" TWO ways:
            // a missing branch on a non-empty repo is 404 (NotFoundException), but a brand-new repo
            // with ZERO commits has an unborn HEAD and returns 409 "Git Repository is empty." for
            // ref/commit lookups. The original catch only handled 404, so the very first "sync back"
            // to a freshly-created empty repo failed with that 409.
            .Catch<HeadInfo, ApiException>(ex =>
                IsMissingOrEmptyRepo(ex)
                    ? Observable.Return(new HeadInfo(null, false, Array.Empty<(string, string)>()))
                    : Observable.Throw<HeadInfo>(ex));

    /// <summary>True when an Octokit error means "this branch has no commit to build on": a missing
    /// branch (404 <see cref="NotFoundException"/>) or a brand-new repo whose HEAD is unborn — GitHub
    /// returns 409 "Git Repository is empty." for ref/commit lookups until the first commit lands.</summary>
    internal static bool IsMissingOrEmptyRepo(ApiException ex)
        => ex is NotFoundException
           || ex.StatusCode == System.Net.HttpStatusCode.Conflict;

    /// <summary>
    /// Resolves the head the mirror builds on when the target branch is missing. Two cases:
    /// <list type="bullet">
    ///   <item>A brand-new EMPTY repo (zero commits) rejects the Git Data API (409 "Git Repository
    ///     is empty." — only the Contents API can create the FIRST commit), so seed a throwaway
    ///     <c>.gitkeep</c> on the branch via the Contents API (creates the branch + first commit),
    ///     then re-read the head. The mirror reconstructs the tree from scratch, dropping the seed.</item>
    ///   <item>A NON-empty repo that merely lacks this branch bases the new branch on the DEFAULT
    ///     branch head — a parent-less commit here would create an ORPHAN branch with no common
    ///     ancestor with the default branch (observed live: an un-PR-able, un-mergeable sync
    ///     branch). The default head's blobs become the preserved tree, so the new branch is
    ///     "default branch + the mirrored subtree" — exactly what a review/merge needs.</item>
    /// </list>
    /// </summary>
    private IObservable<HeadInfo> EnsureInitialCommit(
        IObservableGitHubClient client, string owner, string repo, string branch, string prefix, HeadInfo head)
        => head.RefExists
            ? Observable.Return(head)
            : IsRepoEmpty(client, owner, repo).SelectMany(empty => empty
                ? Http.InvokeObservable(ct => client.Repository.Content.CreateFile(owner, repo, prefix + ".gitkeep",
                        new CreateFileRequest("Initialize repository (MeshWeaver sync)",
                            "Created by MeshWeaver to initialize this repository.\n", branch)))
                    .SelectMany(_ => ReadHead(client, owner, repo, branch))
                : ReadDefaultBranchHead(client, owner, repo, fallback: head));

    /// <summary>
    /// The DEFAULT branch's head with <c>RefExists=false</c> — the base for creating a new branch
    /// with real history (the caller still CREATES the target ref; the commit just parents on the
    /// default head and preserves its tree). Falls back to <paramref name="fallback"/> when the
    /// default branch cannot be resolved (races with repo initialization).
    /// </summary>
    private IObservable<HeadInfo> ReadDefaultBranchHead(
        IObservableGitHubClient client, string owner, string repo, HeadInfo fallback)
        => Http.InvokeObservable(ct => client.Repository.Get(owner, repo))
            .SelectMany(r => string.IsNullOrEmpty(r.DefaultBranch)
                ? Observable.Return(fallback)
                : ReadHead(client, owner, repo, r.DefaultBranch))
            .Select(AsNewBranchBase)
            .Catch<HeadInfo, ApiException>(ex => IsMissingOrEmptyRepo(ex)
                ? Observable.Return(fallback)
                : Observable.Throw<HeadInfo>(ex));

    /// <summary>True when the repo has no commits at all (a freshly-created repo): GitHub lists zero
    /// branches for an empty repo, and some endpoints 409 "Git Repository is empty.".</summary>
    private IObservable<bool> IsRepoEmpty(IObservableGitHubClient client, string owner, string repo)
        // The reactive GetAll streams one Branch per page-item; .ToList() reduces the stream to a
        // single IList so the pool-bridged leaf emits exactly one value (empty ⇔ no branches).
        => Http.InvokeObservable(ct => client.Repository.Branch.GetAll(owner, repo).ToList())
            .Select(branches => branches.Count == 0)
            .Catch<bool, ApiException>(ex => IsMissingOrEmptyRepo(ex)
                ? Observable.Return(true)
                : Observable.Throw<bool>(ex));

    /// <summary>
    /// Resolves a commitish (a branch name OR a commit SHA) to its commit + recursive
    /// blob list. Unlike <see cref="ReadHead"/> this does NOT swallow NotFound — a
    /// re-import at a non-existent commit/branch must surface a clear error.
    /// </summary>
    private IObservable<HeadInfo> ResolveCommitish(IObservableGitHubClient client, string owner, string repo, string commitish)
        => IsSha(commitish)
            ? Http.InvokeObservable(ct => client.Git.Commit.Get(owner, repo, commitish))
                .SelectMany(commit => TreeOf(client, owner, repo, commit))
            : Http.InvokeObservable(ct => client.Git.Reference.Get(owner, repo, $"heads/{commitish}"))
                .SelectMany(reference => Http.InvokeObservable(ct => client.Git.Commit.Get(owner, repo, reference.Object.Sha))
                    .SelectMany(commit => TreeOf(client, owner, repo, commit)));

    private IObservable<HeadInfo> TreeOf(IObservableGitHubClient client, string owner, string repo, Commit commit)
        => Http.InvokeObservable(ct => client.Git.Tree.GetRecursive(owner, repo, commit.Tree.Sha))
            .Select(tree => new HeadInfo(
                commit.Sha, true,
                tree.Tree
                    .Where(i => string.Equals(i.Type.StringValue, "blob", StringComparison.OrdinalIgnoreCase))
                    .Select(i => (i.Path, i.Sha))
                    .ToArray()));

    /// <summary>A 7–40 char all-hex token is treated as a commit SHA; anything else as a branch name.</summary>
    private static bool IsSha(string s) =>
        s.Length is >= 7 and <= 40 && s.All(Uri.IsHexDigit);

    private IObservable<IReadOnlyList<(string Path, string Sha)>> CreateBlobs(
        IObservableGitHubClient client, string owner, string repo, IReadOnlyList<RepoFile> files, string prefix)
    {
        if (files.Count == 0)
            return Observable.Return((IReadOnlyList<(string, string)>)Array.Empty<(string, string)>());
        return files
            .Select(f => Http.InvokeObservable(ct => client.Git.Blob.Create(owner, repo,
                    new NewBlob { Content = f.Content, Encoding = EncodingType.Utf8 }))
                .Select(blob => (Path: prefix + f.Path, blob.Sha)))
            .Merge(8)
            .ToList()
            .Select(list => (IReadOnlyList<(string, string)>)list);
    }

    private IObservable<Octokit.Reference> UpdateRef(
        IObservableGitHubClient client, string owner, string repo, string branch, string commitSha, bool refExists)
        => refExists
            ? Http.InvokeObservable(ct => client.Git.Reference.Update(owner, repo, $"heads/{branch}", new ReferenceUpdate(commitSha)))
            : Http.InvokeObservable(ct => client.Git.Reference.Create(owner, repo, new NewReference($"refs/heads/{branch}", commitSha)));

    private static string DecodeBlob(Blob blob) =>
        string.Equals(blob.Encoding.StringValue, "base64", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(blob.Content))
            : blob.Content;

    private static string NormalizePrefix(string? subdirectory)
    {
        var s = subdirectory?.Trim().Trim('/');
        return string.IsNullOrEmpty(s) ? "" : s + "/";
    }
}
