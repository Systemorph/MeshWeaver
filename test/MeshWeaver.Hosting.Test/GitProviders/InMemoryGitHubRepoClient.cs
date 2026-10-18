#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.GitSync;

namespace MeshWeaver.Hosting.Test.GitProviders;

/// <summary>
/// The GitHub side of the parity suite: an <see cref="IGitHubRepoClient"/> over
/// <see cref="InMemoryGitRemote"/>s keyed by repository URL — the seam every GitSync suite fakes
/// GitHub at (MeshWeaver.Plugins' <c>FakeGitHubRepoClient</c>). It keeps the contract the production
/// GitHub clients keep: mirror within the subdirectory, a missing branch based on the default head,
/// one commit per push even when nothing changed (the git protocol's <c>--allow-empty</c>), a diff
/// that is null when the base is not an ancestor of the head.
/// </summary>
public sealed class InMemoryGitHubRepoClient : IGitHubRepoClient
{
    private ImmutableDictionary<string, InMemoryGitRemote> remotes =
        ImmutableDictionary<string, InMemoryGitRemote>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>The repository at <paramref name="url"/>, created empty on first use.</summary>
    public InMemoryGitRemote Remote(string url)
        => ImmutableInterlocked.GetOrAdd(ref remotes, Normalize(url), _ => new InMemoryGitRemote());

    /// <summary>The tokens each call authenticated with.</summary>
    public ImmutableList<string> Tokens { get; private set; } = [];

    public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => Observable.Defer(() =>
    {
        Tokens = Tokens.Add(request.AccessToken);
        var remote = Remote(request.RepositoryUrl);
        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch.Trim();
        var prefix = Prefix(request.Subdirectory);
        var head = remote.Head(branch);
        if (head is null && !request.CreateBranchIfMissing)
            return Observable.Throw<GitHubPushResult>(new InvalidOperationException(
                $"Branch '{branch}' does not exist and branch auto-create is disabled."));
        var baseSha = head ?? (remote.DefaultBranch is { } d ? remote.Head(d) : null);
        var before = baseSha is not null ? remote.Commits[baseSha].Tree : InMemoryGitRemote.EmptyTree;
        var exported = request.Files.Select(f => prefix + f.Path).ToHashSet(StringComparer.Ordinal);
        var deleted = before.Keys.Count(p => p.StartsWith(prefix, StringComparison.Ordinal) && !exported.Contains(p));
        var commit = remote.CommitTo(branch, tree =>
        {
            var kept = tree.Where(kv => !kv.Key.StartsWith(prefix, StringComparison.Ordinal));
            var mirrored = request.Files.Select(f => new KeyValuePair<string, byte[]>(prefix + f.Path, f.Bytes));
            return kept.Concat(mirrored).ToImmutableSortedDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }, request.CommitMessage, expectedHead: head, checkExpectedHead: true, baseSha: baseSha);
        return Observable.Return(new GitHubPushResult(
            commit.Sha, request.RepositoryUrl, request.Files.Count, deleted, RepoCreated: false));
    });

    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        => Fetch(repositoryUrl, commitish, subdirectory, accessToken, _ => true);

    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken,
        Func<string, bool> pathFilter) => Observable.Defer(() =>
    {
        Tokens = Tokens.Add(accessToken);
        var remote = Remote(repositoryUrl);
        var sha = Resolve(remote, repositoryUrl, commitish);
        var prefix = Prefix(subdirectory);
        var files = remote.Commits[sha].Tree
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => (Relative: kv.Key[prefix.Length..], kv.Value))
            .Where(x => pathFilter(x.Relative))
            .Select(x => RepoFileCodec.FromBytes(x.Relative, x.Value))
            .ToImmutableList();
        return Observable.Return(new RepoSnapshot(sha, files));
    });

    public IObservable<string> GetHeadSha(string repositoryUrl, string commitish, string accessToken)
        => Observable.Defer(() => Observable.Return(Resolve(Remote(repositoryUrl), repositoryUrl, commitish)));

    public IObservable<IReadOnlyList<string>?> GetChangedPaths(
        string repositoryUrl, string baseSha, string headSha, string? subdirectory, string accessToken)
        => Observable.Defer(() =>
        {
            var remote = Remote(repositoryUrl);
            if (remote.FindCommit(baseSha) is not { } from || remote.FindCommit(headSha) is not { } to
                || !remote.Ancestry(to.Sha).Contains(from.Sha, StringComparer.OrdinalIgnoreCase))
                return Observable.Return<IReadOnlyList<string>?>(null);
            var prefix = Prefix(subdirectory);
            var changed = from.Tree.Keys.Union(to.Tree.Keys, StringComparer.Ordinal)
                .Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
                .Where(p => !from.Tree.TryGetValue(p, out var a) || !to.Tree.TryGetValue(p, out var b) || !a.SequenceEqual(b))
                .Select(p => p[prefix.Length..])
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToImmutableList();
            return Observable.Return<IReadOnlyList<string>?>(changed);
        });

    public IObservable<bool> IsAncestor(
        string repositoryUrl, string ancestorSha, string descendantRef, string accessToken)
        => Observable.Defer(() =>
        {
            var remote = Remote(repositoryUrl);
            var descendant = Resolve(remote, repositoryUrl, descendantRef);
            return Observable.Return(remote.Ancestry(descendant).Contains(ancestorSha, StringComparer.OrdinalIgnoreCase));
        });

    public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request) => Observable.Defer(() =>
    {
        var remote = Remote(request.RepositoryUrl);
        var sha = Resolve(remote, request.RepositoryUrl, request.BaseRef);
        return remote.CreateBranch(request.NewBranch, sha)
            ? Observable.Return(new GitHubBranchResult(request.NewBranch, sha))
            : Observable.Throw<GitHubBranchResult>(new InvalidOperationException($"Branch '{request.NewBranch}' exists."));
    });

    public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request) => NotUsed<GitHubPullRequestInfo>();
    public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(string repositoryUrl, int number, string accessToken) => NotUsed<GitHubPullRequestInfo>();
    public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(string repositoryUrl, GitHubIssueState? state, string accessToken) => NotUsed<IReadOnlyList<GitHubIssue>>();
    public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken) => NotUsed<GitHubIssue>();
    public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request) => NotUsed<GitHubIssue>();
    public IObservable<GitHubIssueComment> CommentIssue(string repositoryUrl, int number, string body, string accessToken) => NotUsed<GitHubIssueComment>();
    public IObservable<GitHubIssue> SetIssueState(string repositoryUrl, int number, GitHubIssueState state, string accessToken) => NotUsed<GitHubIssue>();
    public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(string repositoryUrl, PullRequestStatus? state, string accessToken) => NotUsed<IReadOnlyList<GitHubPullRequestSummary>>();
    public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(string repositoryUrl, int number, string accessToken) => NotUsed<GitHubPullRequestDetail>();
    public IObservable<GitHubIssueComment> CommentPullRequest(string repositoryUrl, int number, string body, string accessToken) => NotUsed<GitHubIssueComment>();
    public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request) => NotUsed<GitHubMergeResult>();

    private static IObservable<T> NotUsed<T>()
        => Observable.Throw<T>(new NotSupportedException("the parity suite exercises sync, not pull requests or issues"));

    private static string Resolve(InMemoryGitRemote remote, string url, string commitish)
    {
        var name = string.IsNullOrWhiteSpace(commitish) ? "main" : commitish.Trim();
        return remote.Head(name) ?? remote.FindCommit(name)?.Sha
            ?? throw new InvalidOperationException($"'{name}' is not a branch or commit of {url}.");
    }

    private static string Prefix(string? subdirectory)
        => subdirectory?.Trim().Trim('/') is { Length: > 0 } s ? s + "/" : "";

    private static string Normalize(string url) => url.Trim().TrimEnd('/');
}
