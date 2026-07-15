using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.GitSync;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// In-memory <see cref="IGitHubRepoClient"/> for offline, deterministic tests. Models the
/// real client's contract: a single-commit mirror within the configured subdirectory, a
/// per-commit history (so re-import at an earlier commit returns that earlier state),
/// auto-create of a missing repo, named branches, and pull requests (number, head/base,
/// state). No statics, no network, no randomness — commit shas + PR numbers are incrementing
/// counters so the loop tests are reproducible.
/// </summary>
public sealed class FakeGitHubRepoClient : IGitHubRepoClient
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<RepoFile>> _head = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<RepoFile>> _byCommit = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _headSha = new(StringComparer.Ordinal);
    private readonly HashSet<string> _exists = new(StringComparer.Ordinal);
    // repoKey → (branchName → SHA it points at)
    private readonly Dictionary<string, Dictionary<string, string>> _branches = new(StringComparer.Ordinal);
    // repoKey → list of PRs (number-keyed)
    private readonly Dictionary<string, Dictionary<int, FakePullRequest>> _prs = new(StringComparer.Ordinal);
    // repoKey → issues (number-keyed)
    private readonly Dictionary<string, Dictionary<int, GitHubIssue>> _issues = new(StringComparer.Ordinal);
    private int _counter;
    private int _prNumber;
    private int _issueNumber;
    private long _commentId;

    private sealed record FakePullRequest(
        int Number, string Url, string Title, string? Body,
        string Head, string Base, PullRequestStatus Status);

    /// <summary>The current full tree of a repo (for test assertions).</summary>
    public IReadOnlyList<RepoFile> Tree(string repositoryUrl)
    {
        lock (_lock) return _head.TryGetValue(Key(repositoryUrl), out var t) ? t.ToList() : new List<RepoFile>();
    }

    /// <summary>True when <paramref name="branch"/> exists in the repo (for test assertions).</summary>
    public bool BranchExists(string repositoryUrl, string branch)
    {
        lock (_lock)
            return _branches.TryGetValue(Key(repositoryUrl), out var b) && b.ContainsKey(branch);
    }

    /// <summary>
    /// Simulates GitHub-side state change on an open PR (merge / close) so the status-sync
    /// test can assert the node picks up the new state.
    /// </summary>
    public void SetPullRequestStatus(string repositoryUrl, int number, PullRequestStatus status)
    {
        lock (_lock)
        {
            if (_prs.TryGetValue(Key(repositoryUrl), out var prs) && prs.TryGetValue(number, out var pr))
                prs[number] = pr with { Status = status };
        }
    }

    public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            var key = Key(request.RepositoryUrl);
            var created = _exists.Add(key) && !_head.ContainsKey(key);
            var prefix = Norm(request.Subdirectory);
            var existing = _head.TryGetValue(key, out var cur) ? cur : new List<RepoFile>();

            var newUnder = request.Files.Select(f => f with { Path = prefix + f.Path }).ToList();
            var exportPaths = newUnder.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var outside = existing
                .Where(f => prefix.Length != 0 && !f.Path.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            var deleted = existing.Count(f =>
                (prefix.Length == 0 || f.Path.StartsWith(prefix, StringComparison.Ordinal))
                && !exportPaths.Contains(f.Path));

            var newTree = outside.Concat(newUnder).ToList();
            _head[key] = newTree;
            var sha = NextSha();
            _byCommit[sha] = newTree;
            _headSha[key] = sha;
            // Record the branch ref so CreateBranch / OpenPullRequest can resolve it.
            var branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch;
            Branches(key)[branch] = sha;
            return Observable.Return(new GitHubPushResult(sha, request.RepositoryUrl, request.Files.Count, deleted, created));
        }
    });

    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            var key = Key(repositoryUrl);
            IReadOnlyList<RepoFile> tree;
            string sha;
            if (_byCommit.TryGetValue(commitish, out var byCommit))
            {
                tree = byCommit;
                sha = commitish;
            }
            else if (_head.TryGetValue(key, out var head))
            {
                tree = head;
                sha = _headSha.TryGetValue(key, out var h) ? h : "head";
            }
            else
            {
                return Observable.Throw<RepoSnapshot>(
                    new InvalidOperationException($"Fake repo '{repositoryUrl}' has no commit for '{commitish}'."));
            }

            var prefix = Norm(subdirectory);
            var files = tree
                .Where(f => prefix.Length == 0 || f.Path.StartsWith(prefix, StringComparison.Ordinal))
                .Select(f => f with { Path = prefix.Length == 0 ? f.Path : f.Path[prefix.Length..] })
                .ToList();
            return Observable.Return(new RepoSnapshot(sha, files));
        }
    });

    public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            var key = Key(request.RepositoryUrl);
            var branches = Branches(key);
            var baseRef = string.IsNullOrWhiteSpace(request.BaseRef) ? "main" : request.BaseRef.Trim();
            // Resolve the base ref to a SHA: a known branch name, an existing commit SHA, else error.
            string? sha = branches.TryGetValue(baseRef, out var byBranch) ? byBranch
                : _byCommit.ContainsKey(baseRef) ? baseRef
                : null;
            if (sha is null)
                return Observable.Throw<GitHubBranchResult>(new InvalidOperationException(
                    $"Fake repo '{request.RepositoryUrl}' has no ref '{baseRef}' to branch from."));
            branches[request.NewBranch.Trim()] = sha;
            return Observable.Return(new GitHubBranchResult(request.NewBranch.Trim(), sha));
        }
    });

    public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            var key = Key(request.RepositoryUrl);
            var number = ++_prNumber;
            var (owner, repo) = OctokitGitHubRepoClient.ParseRepoUrl(request.RepositoryUrl);
            var url = $"https://github.com/{owner}/{repo}/pull/{number}";
            var pr = new FakePullRequest(
                number, url, request.Title, request.Body,
                request.HeadBranch, request.BaseBranch, PullRequestStatus.Open);
            Prs(key)[number] = pr;
            return Observable.Return(new GitHubPullRequestInfo(number, url, PullRequestStatus.Open));
        }
    });

    public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
        string repositoryUrl, int number, string accessToken) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            var key = Key(repositoryUrl);
            if (_prs.TryGetValue(key, out var prs) && prs.TryGetValue(number, out var pr))
                return Observable.Return(new GitHubPullRequestInfo(pr.Number, pr.Url, pr.Status));
            return Observable.Throw<GitHubPullRequestInfo>(new InvalidOperationException(
                $"Fake repo '{repositoryUrl}' has no PR #{number}."));
        }
    });

    // ── Issues ───────────────────────────────────────────────────────────────

    /// <summary>Seeds an issue into the fake repo (for sync tests). Returns the assigned number.</summary>
    public int SeedIssue(string repositoryUrl, string title, string? body = null, GitHubIssueState state = GitHubIssueState.Open)
    {
        lock (_lock)
        {
            var key = Key(repositoryUrl);
            var number = ++_issueNumber;
            var (owner, repo) = OctokitGitHubRepoClient.ParseRepoUrl(repositoryUrl);
            Issues(key)[number] = new GitHubIssue
            {
                Number = number, Title = title, Body = body, State = state, AuthorLogin = "tester",
                CommentsCount = 0, Url = $"https://github.com/{owner}/{repo}/issues/{number}",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            return number;
        }
    }

    public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
        string repositoryUrl, GitHubIssueState? state, string accessToken) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            var all = _issues.TryGetValue(Key(repositoryUrl), out var m)
                ? m.Values : Enumerable.Empty<GitHubIssue>();
            var list = all
                .Where(i => state is null || i.State == state)
                // A list read carries no comments (matches the real client).
                .Select(i => i with { Comments = ImmutableList<GitHubIssueComment>.Empty })
                .OrderByDescending(i => i.Number)
                .ToList();
            return Observable.Return((IReadOnlyList<GitHubIssue>)list);
        }
    });

    public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            if (_issues.TryGetValue(Key(repositoryUrl), out var m) && m.TryGetValue(number, out var issue))
                return Observable.Return(issue);
            return Observable.Throw<GitHubIssue>(new InvalidOperationException(
                $"Fake repo '{repositoryUrl}' has no issue #{number}."));
        }
    });

    public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            var key = Key(request.RepositoryUrl);
            var number = ++_issueNumber;
            var (owner, repo) = OctokitGitHubRepoClient.ParseRepoUrl(request.RepositoryUrl);
            var issue = new GitHubIssue
            {
                Number = number, Title = request.Title, Body = request.Body, State = GitHubIssueState.Open,
                AuthorLogin = "tester", Labels = request.Labels, CommentsCount = 0,
                Url = $"https://github.com/{owner}/{repo}/issues/{number}",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            Issues(key)[number] = issue;
            return Observable.Return(issue);
        }
    });

    public IObservable<GitHubIssueComment> CommentIssue(
        string repositoryUrl, int number, string body, string accessToken) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            var key = Key(repositoryUrl);
            if (!_issues.TryGetValue(key, out var m) || !m.TryGetValue(number, out var issue))
                return Observable.Throw<GitHubIssueComment>(new InvalidOperationException(
                    $"Fake repo '{repositoryUrl}' has no issue #{number}."));
            var comment = new GitHubIssueComment(++_commentId, "tester", body, DateTimeOffset.UtcNow, null);
            m[number] = issue with
            {
                CommentsCount = issue.CommentsCount + 1,
                Comments = issue.Comments.Add(comment),
            };
            return Observable.Return(comment);
        }
    });

    // ── Pull requests (richer) ────────────────────────────────────────────────

    public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
        string repositoryUrl, PullRequestStatus? state, string accessToken) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            var all = _prs.TryGetValue(Key(repositoryUrl), out var m)
                ? m.Values : Enumerable.Empty<FakePullRequest>();
            var list = all
                .Where(pr => state is null || MatchesFilter(pr.Status, state.Value))
                .Select(pr => new GitHubPullRequestSummary(
                    pr.Number, pr.Title, "tester", pr.Status, false, pr.Head, pr.Base, pr.Url,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))
                .OrderByDescending(s => s.Number)
                .ToList();
            return Observable.Return((IReadOnlyList<GitHubPullRequestSummary>)list);
        }
    });

    public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
        string repositoryUrl, int number, string accessToken) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            if (_prs.TryGetValue(Key(repositoryUrl), out var m) && m.TryGetValue(number, out var pr))
                return Observable.Return(new GitHubPullRequestDetail(
                    pr.Number, pr.Title, pr.Body, "tester", pr.Status, false, pr.Head, pr.Base,
                    "headsha", pr.Url, Mergeable: true, MergeableState: "clean", CommentsCount: 0,
                    GitHubCheckSummary.Empty, GitHubReviewSummary.Empty));
            return Observable.Throw<GitHubPullRequestDetail>(new InvalidOperationException(
                $"Fake repo '{repositoryUrl}' has no PR #{number}."));
        }
    });

    public IObservable<GitHubIssueComment> CommentPullRequest(
        string repositoryUrl, int number, string body, string accessToken) => Observable.Defer(() =>
    {
        lock (_lock)
            return Observable.Return(new GitHubIssueComment(++_commentId, "tester", body, DateTimeOffset.UtcNow, null));
    });

    public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            var key = Key(request.RepositoryUrl);
            if (_prs.TryGetValue(key, out var m) && m.TryGetValue(request.Number, out var pr))
            {
                m[request.Number] = pr with { Status = PullRequestStatus.Merged };
                return Observable.Return(new GitHubMergeResult(true, "mergedsha", "Pull request successfully merged"));
            }
            return Observable.Throw<GitHubMergeResult>(new InvalidOperationException(
                $"Fake repo '{request.RepositoryUrl}' has no PR #{request.Number}."));
        }
    });

    private static bool MatchesFilter(PullRequestStatus status, PullRequestStatus filter) =>
        filter == PullRequestStatus.Open ? status == PullRequestStatus.Open
        : status is PullRequestStatus.Closed or PullRequestStatus.Merged;

    private Dictionary<int, GitHubIssue> Issues(string key)
    {
        if (!_issues.TryGetValue(key, out var i)) _issues[key] = i = new();
        return i;
    }

    private Dictionary<string, string> Branches(string key)
    {
        if (!_branches.TryGetValue(key, out var b)) _branches[key] = b = new(StringComparer.Ordinal);
        return b;
    }

    private Dictionary<int, FakePullRequest> Prs(string key)
    {
        if (!_prs.TryGetValue(key, out var p)) _prs[key] = p = new();
        return p;
    }

    private string NextSha() => (++_counter).ToString("x").PadLeft(40, '0');

    private static string Key(string url)
    {
        var (owner, repo) = OctokitGitHubRepoClient.ParseRepoUrl(url);
        return $"{owner}/{repo}".ToLowerInvariant();
    }

    private static string Norm(string? s)
    {
        var t = s?.Trim().Trim('/');
        return string.IsNullOrEmpty(t) ? "" : t + "/";
    }
}
