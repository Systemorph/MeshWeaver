using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Octokit;

namespace MeshWeaver.GitSync;

/// <summary>
/// Production <see cref="IGitHubRepoClient"/> over the Octokit Git Data API. Every
/// Octokit <c>…Async</c> leaf is bridged through the <see cref="IoPoolNames.Http"/>
/// pool (<c>pool.Invoke(ct =&gt; client.X(…))</c>) and composed reactively — no
/// <c>async</c>/<c>await</c>/<c>Task</c> escapes a method signature, per
/// <c>Doc/Architecture/ControlledIoPooling.md</c>.
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

    private static GitHubClient Client(string token) =>
        new(Product) { Credentials = new Credentials(token) };

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

    public IObservable<GitHubPushResult> Push(GitHubPushRequest request)
    {
        var (owner, repo) = ParseRepoUrl(request.RepositoryUrl);
        var client = Client(request.AccessToken);
        var prefix = NormalizePrefix(request.Subdirectory);
        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch;

        return EnsureRepo(client, owner, repo, request.CreatePrivateIfMissing)
            .SelectMany(repoCreated => ReadHead(client, owner, repo, branch)
                .SelectMany(head =>
                {
                    if (!head.RefExists && !request.CreateBranchIfMissing)
                        return Observable.Throw<GitHubPushResult>(new InvalidOperationException(
                            $"Branch '{branch}' does not exist and branch auto-create is disabled."));

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

                            return Http.Invoke(ct => client.Git.Tree.Create(owner, repo, newTree))
                                .SelectMany(tree =>
                                {
                                    var commit = head.CommitSha is { Length: > 0 } parent
                                        ? new NewCommit(request.CommitMessage, tree.Sha, parent)
                                        : new NewCommit(request.CommitMessage, tree.Sha, Array.Empty<string>());
                                    var who = new Committer(request.AuthorName, request.AuthorEmail, DateTimeOffset.UtcNow);
                                    commit.Author = who;
                                    commit.Committer = who;
                                    return Http.Invoke(ct => client.Git.Commit.Create(owner, repo, commit));
                                })
                                .SelectMany(commit => UpdateRef(client, owner, repo, branch, commit.Sha, head.RefExists)
                                    .Select(_ => new GitHubPushResult(
                                        commit.Sha, request.RepositoryUrl,
                                        request.Files.Count, deleted, repoCreated)));
                        });
                }));
    }

    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var client = Client(accessToken);
        var prefix = NormalizePrefix(subdirectory);
        var commitRef = string.IsNullOrWhiteSpace(commitish) ? "main" : commitish.Trim();

        return ResolveCommitish(client, owner, repo, commitRef)
            .SelectMany(head =>
            {
                var blobs = head.ExistingBlobs
                    .Where(e => prefix.Length == 0 || e.Path.StartsWith(prefix, StringComparison.Ordinal))
                    .ToArray();
                if (blobs.Length == 0)
                    return Observable.Return(new RepoSnapshot(head.CommitSha!, Array.Empty<RepoFile>()));

                return blobs
                    .Select(e => Http.Invoke(ct => client.Git.Blob.Get(owner, repo, e.Sha))
                        .Select(blob => new RepoFile(
                            prefix.Length == 0 ? e.Path : e.Path[prefix.Length..],
                            DecodeBlob(blob))))
                    .Merge(8)
                    .ToList()
                    .Select(list => new RepoSnapshot(head.CommitSha!, (IReadOnlyList<RepoFile>)list));
            });
    }

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
                return Http.Invoke(ct => client.Git.Reference.Create(
                        owner, repo, new NewReference($"refs/heads/{newBranch}", sha)))
                    .Select(reference => new GitHubBranchResult(newBranch, reference.Object.Sha));
            });
    }

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
        return Http.Invoke(ct => client.PullRequest.Create(owner, repo, newPr))
            .Select(ToInfo);
    }

    public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
        string repositoryUrl, int number, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var client = Client(accessToken);
        return Http.Invoke(ct => client.PullRequest.Get(owner, repo, number))
            .Select(ToInfo);
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

    /// <summary>Resolves a commitish (branch name OR SHA) to its commit SHA — cheap, no tree read.</summary>
    private IObservable<string> ResolveRefSha(GitHubClient client, string owner, string repo, string commitish)
        => IsSha(commitish)
            ? Http.Invoke(ct => client.Git.Commit.Get(owner, repo, commitish)).Select(c => c.Sha)
            : Http.Invoke(ct => client.Git.Reference.Get(owner, repo, $"heads/{commitish}"))
                .Select(reference => reference.Object.Sha);

    private sealed record HeadInfo(string? CommitSha, bool RefExists, IReadOnlyList<(string Path, string Sha)> ExistingBlobs);

    /// <summary>Ensures the repo exists; creates it private (under the user or the org) when missing.</summary>
    private IObservable<bool> EnsureRepo(GitHubClient client, string owner, string repo, bool createIfMissing)
        => Http.Invoke(ct => client.Repository.Get(owner, repo))
            .Select(_ => false)
            .Catch<bool, NotFoundException>(_ =>
            {
                if (!createIfMissing)
                    return Observable.Throw<bool>(new InvalidOperationException(
                        $"Repository {owner}/{repo} does not exist and auto-create is disabled."));
                var newRepo = new NewRepository(repo) { Private = true, AutoInit = false };
                logger?.LogInformation("Creating private GitHub repo {Owner}/{Repo}.", owner, repo);
                return Http.Invoke(ct => client.User.Current())
                    .SelectMany(me => string.Equals(me.Login, owner, StringComparison.OrdinalIgnoreCase)
                        ? Http.Invoke(ct => client.Repository.Create(newRepo))
                        : Http.Invoke(ct => client.Repository.Create(owner, newRepo)))
                    .Select(_ => true);
            });

    /// <summary>Reads the branch head commit + its recursive blob list. Tolerates an empty repo (no ref yet).</summary>
    private IObservable<HeadInfo> ReadHead(GitHubClient client, string owner, string repo, string branch)
        => Http.Invoke(ct => client.Git.Reference.Get(owner, repo, $"heads/{branch}"))
            .SelectMany(reference => Http.Invoke(ct => client.Git.Commit.Get(owner, repo, reference.Object.Sha))
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
    /// Resolves a commitish (a branch name OR a commit SHA) to its commit + recursive
    /// blob list. Unlike <see cref="ReadHead"/> this does NOT swallow NotFound — a
    /// re-import at a non-existent commit/branch must surface a clear error.
    /// </summary>
    private IObservable<HeadInfo> ResolveCommitish(GitHubClient client, string owner, string repo, string commitish)
        => IsSha(commitish)
            ? Http.Invoke(ct => client.Git.Commit.Get(owner, repo, commitish))
                .SelectMany(commit => TreeOf(client, owner, repo, commit))
            : Http.Invoke(ct => client.Git.Reference.Get(owner, repo, $"heads/{commitish}"))
                .SelectMany(reference => Http.Invoke(ct => client.Git.Commit.Get(owner, repo, reference.Object.Sha))
                    .SelectMany(commit => TreeOf(client, owner, repo, commit)));

    private IObservable<HeadInfo> TreeOf(GitHubClient client, string owner, string repo, Commit commit)
        => Http.Invoke(ct => client.Git.Tree.GetRecursive(owner, repo, commit.Tree.Sha))
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
        GitHubClient client, string owner, string repo, IReadOnlyList<RepoFile> files, string prefix)
    {
        if (files.Count == 0)
            return Observable.Return((IReadOnlyList<(string, string)>)Array.Empty<(string, string)>());
        return files
            .Select(f => Http.Invoke(ct => client.Git.Blob.Create(owner, repo,
                    new NewBlob { Content = f.Content, Encoding = EncodingType.Utf8 }))
                .Select(blob => (Path: prefix + f.Path, blob.Sha)))
            .Merge(8)
            .ToList()
            .Select(list => (IReadOnlyList<(string, string)>)list);
    }

    private IObservable<Octokit.Reference> UpdateRef(
        GitHubClient client, string owner, string repo, string branch, string commitSha, bool refExists)
        => refExists
            ? Http.Invoke(ct => client.Git.Reference.Update(owner, repo, $"heads/{branch}", new ReferenceUpdate(commitSha)))
            : Http.Invoke(ct => client.Git.Reference.Create(owner, repo, new NewReference($"refs/heads/{branch}", commitSha)));

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
