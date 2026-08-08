#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.GitSync;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// A read-only in-memory <see cref="IGitHubRepoClient"/>: one immutable tree at one commit, served
/// with the same subdirectory semantics the real client has (files come back RELATIVE to the
/// requested subdirectory). That is the entire surface a plugin-catalog scan and a first import
/// touch — everything that would WRITE to the repo throws, which is itself part of the contract
/// under test: an auto-provisioned consumer must never push, branch or create a repository.
/// </summary>
public sealed class StubRepoClient(string repositoryUrl, string commitSha, IReadOnlyList<RepoFile> files)
    : IGitHubRepoClient
{
    /// <summary>The tree this stub serves (test assertions read it).</summary>
    public IReadOnlyList<RepoFile> Files { get; } = files;

    public IObservable<RepoSnapshot> Fetch(
        string url, string commitish, string? subdirectory, string accessToken) => Observable.Defer(() =>
    {
        if (!string.Equals(Key(url), Key(repositoryUrl), StringComparison.OrdinalIgnoreCase))
            return Observable.Throw<RepoSnapshot>(new InvalidOperationException(
                $"Stub repo has no '{url}' (it serves '{repositoryUrl}')."));

        var prefix = string.IsNullOrWhiteSpace(subdirectory) ? "" : subdirectory.Trim().Trim('/') + "/";
        var scoped = Files
            .Where(f => prefix.Length == 0 || f.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => f with { Path = prefix.Length == 0 ? f.Path : f.Path[prefix.Length..] })
            .ToList();
        return Observable.Return(new RepoSnapshot(commitSha, scoped));
    });

    private static string Key(string url) => url.Trim().TrimEnd('/')
        .Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase)
        .Replace("git@github.com:", "", StringComparison.OrdinalIgnoreCase)
        .Replace(".git", "", StringComparison.OrdinalIgnoreCase);

    // ── everything that writes: deliberately unavailable ──────────────────────
    // A discovery-provisioned Space is import-only and never creates a branch or a repository.
    // Throwing here means a regression that started pushing would FAIL a test rather than pass one.

    private static Exception ReadOnly([System.Runtime.CompilerServices.CallerMemberName] string member = "") =>
        new NotSupportedException(
            $"StubRepoClient is read-only: '{member}' must never be reached by a discovery scan.");

    public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => throw ReadOnly();

    public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request) => throw ReadOnly();

    public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request)
        => throw ReadOnly();

    public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(string url, int number, string token)
        => throw ReadOnly();

    public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(string url, GitHubIssueState? state, string token)
        => throw ReadOnly();

    public IObservable<GitHubIssue> GetIssue(string url, int number, string token) => throw ReadOnly();

    public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request) => throw ReadOnly();

    public IObservable<GitHubIssueComment> CommentIssue(string url, int number, string body, string token)
        => throw ReadOnly();

    public IObservable<GitHubIssue> SetIssueState(string url, int number, GitHubIssueState state, string token)
        => throw ReadOnly();

    public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
        string url, PullRequestStatus? state, string token) => throw ReadOnly();

    public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(string url, int number, string token)
        => throw ReadOnly();

    public IObservable<GitHubIssueComment> CommentPullRequest(string url, int number, string body, string token)
        => throw ReadOnly();

    public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request)
        => throw ReadOnly();
}
