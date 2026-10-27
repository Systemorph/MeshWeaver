using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// Production <see cref="IGitHubRepoClient"/> over GitHub's REST API (the Git Data, repository,
/// issue and pull-request endpoints). Every GitHub leaf is one <see cref="GitHubRestApi"/> request
/// run inside the <see cref="IoPoolNames.Http"/> pool (the transport owns that boundary)
/// and composed reactively — no <c>async</c>/<c>await</c>/<c>Task</c> escapes a method signature,
/// per <c>Doc/Architecture/ControlledIoPooling.md</c>; the pool keeps every request
/// concurrency-bounded and off the hub scheduler (it would otherwise deadlock a hub/grain turn).
///
/// <para>🚨 <b>The name is historical.</b> This client was built on Octokit / Octokit.Reactive and
/// kept its type name when it moved to a plain <see cref="HttpClient"/> (Doc/Architecture/GitHubSync
/// → "The GitHub REST transport"), because satellite repositories construct it, resolve it from DI
/// and call <see cref="ParseRepoUrl"/> by this name. Failures are <see cref="GitHubApiException"/>s
/// (typed per status), no longer Octokit's exception family.</para>
///
/// <para>Export uses a single commit: blob per file → one tree → commit → update
/// ref. Mirror is achieved by reconstructing the tree from scratch (no base_tree):
/// within the configured subdirectory the tree contains exactly the exported files
/// (so removed files vanish); blobs outside the subdirectory are carried over by
/// their existing sha, so the rest of the repo is untouched.</para>
/// </summary>
/// <param name="ioPools">The pool registry; every request runs on its <see cref="IoPoolNames.Http"/> pool.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="httpClient">The client to send with. Null builds one whose handler does NOT follow
/// redirects itself — the transport follows them and keeps the credential on the same host (see
/// <see cref="GitHubRestApi"/>). An injected client should be built the same way.</param>
public sealed class OctokitGitHubRepoClient(
    IoPoolRegistry ioPools,
    ILogger<OctokitGitHubRepoClient>? logger = null,
    HttpClient? httpClient = null)
    : IGitHubRepoClient
{
    private const string BlobMode = "100644";

    /// <summary>
    /// The pre-<see cref="HttpClient"/> constructor signature, kept explicitly so an assembly
    /// compiled against it (a satellite module built on an older platform set) still binds at run
    /// time — an optional parameter added to the primary constructor would change its metadata
    /// signature and fault such a caller with <see cref="MissingMethodException"/>.
    /// </summary>
    /// <param name="ioPools">The pool registry.</param>
    /// <param name="logger">Optional logger.</param>
    public OctokitGitHubRepoClient(IoPoolRegistry ioPools, ILogger<OctokitGitHubRepoClient>? logger)
        : this(ioPools, logger, httpClient: null)
    {
    }

    // An empty token means ANONYMOUS access (a public repo, e.g. the plugin registry serving a
    // public plugins repo with no App identity configured): the transport then sends no
    // Authorization header at all — unauthenticated, lower rate limit, but valid.
    // The transport owns the HTTP pool boundary: every member it exposes is an observable whose
    // exchange runs inside IoPoolNames.Http.
    private readonly GitHubRestApi api = new(httpClient ?? GitHubRestApi.CreateDefaultClient(), ioPools);

    /// <summary>One pooled request, deserialized as <typeparamref name="T"/>.</summary>
    private IObservable<T> Call<T>(HttpMethod method, string path, string token, object? body = null)
        => api.Send<T>(method, path, token, body);

    /// <summary>A whole listing (every page, each pooled), as ONE emission.</summary>
    private IObservable<ImmutableList<T>> ListAll<T>(string path, string token)
        => api.GetAll<T>(path, token);

    private static string RepoPath(string owner, string repo)
        => $"repos/{GitHubRestApi.Escape(owner)}/{GitHubRestApi.Escape(repo)}";

    private static string Number(int number) => number.ToString(CultureInfo.InvariantCulture);

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

    /// <inheritdoc />
    /// <remarks>
    /// 🚨 <b>This is the call that survives a RENAME.</b> GitHub answers
    /// <c>GET /repos/{owner}/{repo}</c> for a repository's OLD name with a <c>301</c> to its current
    /// location, and the transport follows it (with the credential) — so the repository object that
    /// comes back carries the name the repository announces itself under TODAY, which is the name
    /// every webhook payload uses. Reading <c>Owner.Login</c> + <c>Name</c> off that RESPONSE (never
    /// re-parsing the url we asked with) is what turns <c>Systemorph/education</c> into
    /// <c>Systemorph/MeshWeaver.Education</c> (#1856).
    ///
    /// <para>The call ERRORS — a 404 for a repository this token cannot see, a transport fault —
    /// rather than emitting null: "unreachable" is a fact the CALLER must classify (a private
    /// repository read with no credential is not the same as one that does not exist), and
    /// swallowing it here would hide a permanently mis-credentialled resolution behind a
    /// permanently cached "unknown".</para>
    /// </remarks>
    public IObservable<RepoIdentity?> GetCanonicalRepository(string repositoryUrl, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        return Call<GitHubWireRepository>(HttpMethod.Get, RepoPath(owner, repo), accessToken)
            .Select(r => r?.Owner?.Login is { Length: > 0 } login && r.Name is { Length: > 0 } name
                ? new RepoIdentity(login, name)
                : null)
            .Do(id => logger?.LogDebug(
                "Canonical identity of {Url} resolved to {Identity}.",
                repositoryUrl, id?.ToString() ?? "(unknown)"));
    }

    /// <summary>
    /// Mirrors the request's files into the repo as a single commit (blob → tree → commit →
    /// update ref), creating the repo and/or branch when missing per the request flags.
    /// </summary>
    /// <param name="request">The push/mirror request (repo, branch, files, author, token, create flags).</param>
    /// <returns>An observable emitting the push outcome (commit SHA, files written/deleted, repo-created flag).</returns>
    public IObservable<GitHubPushResult> Push(GitHubPushRequest request)
    {
        var (owner, repo) = ParseRepoUrl(request.RepositoryUrl);
        var token = request.AccessToken;
        var prefix = NormalizePrefix(request.Subdirectory);
        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch;

        return EnsureRepo(token, owner, repo, request.CreatePrivateIfMissing)
            .SelectMany(repoCreated => ReadHead(token, owner, repo, branch)
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
                    return EnsureInitialCommit(token, owner, repo, branch, prefix, head0)
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

                    return CreateBlobs(token, owner, repo, request.Files, prefix)
                        .SelectMany(exportEntries =>
                        {
                            // No base_tree: the tree is reconstructed from scratch (see the type remarks).
                            var newTree = new GitHubWireNewTree(preserved.Concat(exportEntries)
                                .Select(e => new GitHubWireNewTreeItem(e.Path, BlobMode, "blob", e.Sha))
                                .ToImmutableList());

                            return Call<GitHubWireCreated>(HttpMethod.Post, RepoPath(owner, repo) + "/git/trees", token, newTree)
                                .SelectMany(tree =>
                                {
                                    var parents = head.CommitSha is { Length: > 0 } parent
                                        ? ImmutableList.Create(parent)
                                        : ImmutableList<string>.Empty;
                                    var who = new GitHubWireSignature(request.AuthorName, request.AuthorEmail,
                                        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                                    var commit = new GitHubWireNewCommit(request.CommitMessage, tree.Sha, parents, who, who);
                                    return Call<GitHubWireCreated>(HttpMethod.Post, RepoPath(owner, repo) + "/git/commits", token, commit);
                                })
                                .SelectMany(commit => UpdateRef(token, owner, repo, branch, commit.Sha, head.RefExists)
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
        var token = accessToken;
        var prefix = NormalizePrefix(subdirectory);
        var commitRef = string.IsNullOrWhiteSpace(commitish) ? "main" : commitish.Trim();

        return ResolveCommitish(token, owner, repo, commitRef)
            .SelectMany(head =>
            {
                var blobs = head.ExistingBlobs
                    .Where(e => prefix.Length == 0 || e.Path.StartsWith(prefix, StringComparison.Ordinal))
                    .Where(e => pathFilter(prefix.Length == 0 ? e.Path : e.Path[prefix.Length..]))
                    .ToArray();
                // 🚨 The tree read's completeness travels with the snapshot (issue #3589). An empty
                // file list from a TRUNCATED tree is the dangerous case: it looks exactly like "the
                // repository carries nothing", and a FullReplace import reads that as "everything
                // here was deleted". The flag is what stops that inference downstream.
                if (blobs.Length == 0)
                    return Observable.Return(new RepoSnapshot(head.CommitSha!, Array.Empty<RepoFile>())
                        { ListingIsComplete = head.ListingIsComplete });

                return blobs
                    .Select(e => Call<GitHubWireBlob>(HttpMethod.Get,
                            RepoPath(owner, repo) + "/git/blobs/" + GitHubRestApi.Escape(e.Sha), token)
                        .Select(blob => ToRepoFile(
                            prefix.Length == 0 ? e.Path : e.Path[prefix.Length..],
                            blob)))
                    .MergeBounded(8)
                    .ToList()
                    .Select(list => new RepoSnapshot(head.CommitSha!, (IReadOnlyList<RepoFile>)list)
                        { ListingIsComplete = head.ListingIsComplete });
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
        var commitRef = string.IsNullOrWhiteSpace(commitish) ? "main" : commitish.Trim();
        return ResolveRefSha(accessToken, owner, repo, commitRef);
    }

    // The GitHub compare endpoint returns at most this many files in one response; beyond it the
    // file list is TRUNCATED, so we cannot trust it as the complete change set → full-import instead.
    private const int CompareFileCap = 300;

    /// <inheritdoc />
    public IObservable<IReadOnlyList<string>?> GetChangedPaths(
        string repositoryUrl, string baseSha, string headSha, string? subdirectory, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(baseSha) || string.IsNullOrWhiteSpace(headSha))
            return Observable.Return<IReadOnlyList<string>?>(null);
        if (string.Equals(baseSha, headSha, StringComparison.Ordinal))
            return Observable.Return<IReadOnlyList<string>?>(Array.Empty<string>());

        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var prefix = NormalizePrefix(subdirectory);

        return Compare(owner, repo, baseSha, headSha, accessToken)
            .Select(cmp =>
            {
                // Only trust the diff when the head DESCENDS from the base (or equals it). "behind" /
                // "diverged" means the base is NOT an ancestor — a force-push or history rewrite — so
                // the file list is not the cumulative change set; and a truncated list (> the cap)
                // is incomplete. Either way return null → the caller full-imports (never a silent miss).
                var status = cmp.Status;
                if (!string.Equals(status, "ahead", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(status, "identical", StringComparison.OrdinalIgnoreCase))
                    return null;
                // An omitted file list is not a known-empty diff. The endpoint can also cap the
                // list at 300 entries; either answer is incomplete, so import the full snapshot.
                var files = cmp.Files;
                if (files is null || files.Count >= CompareFileCap)
                    return null;
                var changed = new List<string>();
                foreach (var f in files)
                {
                    AddIfUnderPrefix(changed, f.Filename, prefix);
                    // A rename reports the NEW filename; its OLD path must also count as changed so the
                    // stale node is pruned/re-created (prune handles the delete of the absent old file).
                    AddIfUnderPrefix(changed, f.PreviousFilename, prefix);
                }
                return (IReadOnlyList<string>)changed;
            })
            // A compare failure (network, an unknown SHA after a rebase, rate limit) is NOT "nothing
            // changed" — fall back to a full import so an update is never silently dropped.
            .Catch<IReadOnlyList<string>?, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "GitHub compare {Base}...{Head} on {Repo} failed — falling back to a full import.",
                    baseSha, headSha, repositoryUrl);
                return Observable.Return<IReadOnlyList<string>?>(null);
            });
    }

    /// <inheritdoc />
    /// <remarks>One compare call, <c>ancestor...descendant</c>: GitHub answers <c>ahead</c> or
    /// <c>identical</c> exactly when the descendant contains the ancestor. <c>behind</c>,
    /// <c>diverged</c>, an unknown sha or a failed call establish nothing and answer false.</remarks>
    public IObservable<bool> IsAncestor(
        string repositoryUrl, string ancestorSha, string descendantRef, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(ancestorSha) || string.IsNullOrWhiteSpace(descendantRef))
            return Observable.Return(false);
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        return Compare(owner, repo, ancestorSha, descendantRef, accessToken)
            .Select(cmp =>
            {
                var status = cmp.Status;
                return string.Equals(status, "ahead", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(status, "identical", StringComparison.OrdinalIgnoreCase);
            })
            .Catch<bool, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "GitHub compare {Ancestor}...{Descendant} on {Repo} failed — ancestry not established.",
                    ancestorSha, descendantRef, repositoryUrl);
                return Observable.Return(false);
            });
    }

    /// <summary><c>GET /repos/{owner}/{repo}/compare/{base}...{head}</c> — one response, never
    /// paginated (the <see cref="CompareFileCap"/> reasoning depends on that). Both refs are
    /// URL-encoded whole, a branch's <c>/</c> included, as Octokit encoded them.</summary>
    private IObservable<GitHubWireCompare> Compare(string owner, string repo, string @base, string head, string token)
        => Call<GitHubWireCompare>(HttpMethod.Get,
            $"{RepoPath(owner, repo)}/compare/{GitHubRestApi.Escape(@base)}...{GitHubRestApi.Escape(head)}", token);

    private static void AddIfUnderPrefix(List<string> into, string? repoPath, string prefix)
    {
        if (string.IsNullOrEmpty(repoPath)) return;
        if (prefix.Length == 0) { into.Add(repoPath); return; }
        if (repoPath.StartsWith(prefix, StringComparison.Ordinal))
            into.Add(repoPath[prefix.Length..]);
    }

    /// <summary>Creates a branch from an existing ref (branch name or SHA) resolved to its commit.</summary>
    /// <param name="request">The create-branch request (repo, new branch, base ref, token).</param>
    /// <returns>An observable emitting the new branch name and the commit SHA it points at.</returns>
    public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request)
    {
        var (owner, repo) = ParseRepoUrl(request.RepositoryUrl);
        var token = request.AccessToken;
        var baseRef = string.IsNullOrWhiteSpace(request.BaseRef) ? "main" : request.BaseRef.Trim();
        var newBranch = request.NewBranch.Trim();
        if (string.IsNullOrEmpty(newBranch))
            return Observable.Throw<GitHubBranchResult>(new ArgumentException("New branch name is required.", nameof(request)));

        return ResolveRefSha(token, owner, repo, baseRef)
            .SelectMany(sha =>
            {
                logger?.LogInformation("Creating branch {Branch} from {BaseRef} ({Sha}) in {Owner}/{Repo}.",
                    newBranch, baseRef, sha[..Math.Min(8, sha.Length)], owner, repo);
                return CreateRef(token, owner, repo, newBranch, sha)
                    .Select(reference => new GitHubBranchResult(newBranch, reference.Object.Sha));
            });
    }

    /// <summary>Opens a pull request <c>head → base</c> on GitHub.</summary>
    /// <param name="request">The open-PR request (repo, title, body, head/base branches, token).</param>
    /// <returns>An observable emitting the opened pull request's number, URL and state.</returns>
    public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request)
    {
        var (owner, repo) = ParseRepoUrl(request.RepositoryUrl);
        var baseBranch = string.IsNullOrWhiteSpace(request.BaseBranch) ? "main" : request.BaseBranch.Trim();
        var head = request.HeadBranch?.Trim();
        if (string.IsNullOrEmpty(head))
            return Observable.Throw<GitHubPullRequestInfo>(new ArgumentException("Head branch is required.", nameof(request)));

        var newPr = new GitHubWireNewPullRequest(request.Title, head, baseBranch, request.Body ?? "");
        logger?.LogInformation("Opening PR {Head} → {Base} in {Owner}/{Repo}.", head, baseBranch, owner, repo);
        return Call<GitHubWirePullRequest>(HttpMethod.Post, RepoPath(owner, repo) + "/pulls", request.AccessToken, newPr)
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
        return Call<GitHubWirePullRequest>(HttpMethod.Get, $"{RepoPath(owner, repo)}/pulls/{Number(number)}", accessToken)
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
        // Every page, AND the listing includes pull requests (a PR is an issue on GitHub); drop
        // those and map. Sort and direction are Octokit's request defaults, so the order is unchanged.
        return ListAll<GitHubWireIssue>(
                $"{RepoPath(owner, repo)}/issues?state={MapStateFilter(state)}&sort=created&direction=desc", accessToken)
            .Select(list => (IReadOnlyList<GitHubIssue>)list
                .Where(i => i.PullRequest is null)
                .Select(ToIssue)
                .ToImmutableList());
    }

    /// <inheritdoc />
    public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var issuePath = $"{RepoPath(owner, repo)}/issues/{Number(number)}";
        return Call<GitHubWireIssue>(HttpMethod.Get, issuePath, accessToken)
            .SelectMany(issue => ListAll<GitHubWireComment>(issuePath + "/comments", accessToken)
                .Select(comments => ToIssue(issue) with
                {
                    Comments = comments.Select(ToComment).ToImmutableList(),
                    // Earned, not inherited: this is the one read that actually received the list.
                    CommentsAreComplete = true,
                })
                // 🚨 The comments leg 404s for an issue that was TRANSFERRED: GitHub's redirect
                // covers the issue resource and not its sub-resources, so the first leg already
                // succeeded and carries the issue's new number and repository. Faulting the whole
                // call here threw away a successful read of a real issue and made a transferred
                // issue indistinguishable from a deleted one — measured 2026-09-17 on
                // Systemorph/MeshWeaver#2950, now Systemorph/MeshWeaver.Plugins#1139, which is how
                // Admin/_LogIncident/9b70b639c4e77af3 sat at Failed / "Not Found" from 2026-09-01
                // (MeshWeaver#4629, MeshWeaver.Plugins#2028).
                //
                // Keep what was read and DECLARE the gap rather than swallowing it: a caller whose
                // subject is the comments can still tell, because CommentsAreComplete says so.
                // Only the comments leg is forgiven — a 404 on the ISSUE itself still faults.
                // CommentsAreComplete stays at its default false — the comments are exactly what
                // could not be read here.
                .Catch((GitHubNotFoundException _) => Observable.Return(ToIssue(issue))));
    }

    /// <inheritdoc />
    public IObservable<GitHubIssue?> FindIssueState(
        string repositoryUrl, int number, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        // One request, no comments: this answers the state question for a transferred issue too,
        // and the emitted issue's Number/Url name its CURRENT home, so a caller holding a stale
        // reference can re-point rather than re-file (MeshWeaver#4629).
        return Call<GitHubWireIssue>(HttpMethod.Get, $"{RepoPath(owner, repo)}/issues/{Number(number)}", accessToken)
            // No comments were requested, so CommentsAreComplete stays false by default.
            .Select(issue => (GitHubIssue?)ToIssue(issue))
            // The number names no issue at all — a real absence, and the one answer a caller may
            // act on by clearing its link.
            .Catch((GitHubNotFoundException _) => Observable.Return<GitHubIssue?>(null));
    }

    /// <inheritdoc />
    public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request)
    {
        var (owner, repo) = ParseRepoUrl(request.RepositoryUrl);
        var newIssue = new GitHubWireNewIssue(request.Title, request.Body ?? "", request.Labels);
        logger?.LogInformation("Creating issue '{Title}' in {Owner}/{Repo}.", request.Title, owner, repo);
        return Call<GitHubWireIssue>(HttpMethod.Post, RepoPath(owner, repo) + "/issues", request.AccessToken, newIssue)
            .Select(ToIssue);
    }

    /// <inheritdoc />
    public IObservable<GitHubIssueComment> CommentIssue(
        string repositoryUrl, int number, string body, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        return Call<GitHubWireComment>(HttpMethod.Post,
                $"{RepoPath(owner, repo)}/issues/{Number(number)}/comments", accessToken, new GitHubWireNewComment(body))
            .Select(ToComment);
    }

    /// <inheritdoc />
    public IObservable<GitHubIssue> SetIssueState(
        string repositoryUrl, int number, GitHubIssueState state, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var update = new GitHubWireIssueUpdate(state == GitHubIssueState.Open ? "open" : "closed");
        logger?.LogInformation("Setting issue #{Number} in {Owner}/{Repo} to {State}.",
            number, owner, repo, state);
        return Call<GitHubWireIssue>(HttpMethod.Patch, $"{RepoPath(owner, repo)}/issues/{Number(number)}", accessToken, update)
            .Select(ToIssue);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Pull requests (richer: list / detail / comment / merge)
    // ══════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
        string repositoryUrl, PullRequestStatus? state, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        // Sort and direction are Octokit's PullRequestRequest defaults, so the order is unchanged.
        return ListAll<GitHubWirePullRequest>(
                $"{RepoPath(owner, repo)}/pulls?state={MapPrStateFilter(state)}&sort=created&direction=desc", accessToken)
            .Select(list => (IReadOnlyList<GitHubPullRequestSummary>)list.Select(ToSummary).ToImmutableList());
    }

    /// <inheritdoc />
    public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
        string repositoryUrl, int number, string accessToken)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        var prPath = $"{RepoPath(owner, repo)}/pulls/{Number(number)}";
        return Call<GitHubWirePullRequest>(HttpMethod.Get, prPath, accessToken)
            .SelectMany(pr =>
            {
                var headSha = pr.Head?.Sha;
                var reviews = ListAll<GitHubWireReview>(prPath + "/reviews", accessToken)
                    .Select(SummarizeReviews);
                // Checks live over the head commit; a repo with no checks / a token lacking the
                // checks scope returns empty rather than failing the whole detail read.
                var checks = string.IsNullOrEmpty(headSha)
                    ? Observable.Return(GitHubCheckSummary.Empty)
                    : api.GetAll<GitHubWireCheckRuns, GitHubWireCheckRun>(
                            $"{RepoPath(owner, repo)}/commits/{GitHubRestApi.Escape(headSha)}/check-runs",
                            accessToken, page => page.CheckRuns)
                        .Select(SummarizeChecks)
                        // Only "no checks configured / no checks:read scope" (404 / 403) degrade to empty;
                        // a real error (auth, 5xx) must surface, not be silently hidden as "no checks".
                        .Catch<GitHubCheckSummary, GitHubApiException>(ex =>
                            ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
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
        var merge = new GitHubWireMerge(
            request.CommitTitle,
            request.CommitMessage,
            request.Method switch
            {
                GitHubMergeMethod.Squash => "squash",
                GitHubMergeMethod.Rebase => "rebase",
                _ => "merge",
            });
        logger?.LogInformation("Merging PR #{Number} in {Owner}/{Repo} ({Method}).",
            request.Number, owner, repo, request.Method);
        return Call<GitHubWireMergeResult>(HttpMethod.Put,
                $"{RepoPath(owner, repo)}/pulls/{Number(request.Number)}/merge", request.AccessToken, merge)
            .Select(m => new GitHubMergeResult(m.Merged, m.Sha, m.Message));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Maps a GitHub pull request to our merged-state info record.</summary>
    private static GitHubPullRequestInfo ToInfo(GitHubWirePullRequest pr) =>
        new(pr.Number, pr.HtmlUrl ?? "", MapStatus(pr));

    /// <summary>
    /// GitHub <c>state</c> + merged → <see cref="PullRequestStatus"/>. Merged is read off
    /// <c>merged_at</c> as well as <c>merged</c>: a LISTING row carries no <c>merged</c> field, and
    /// Octokit derived its <c>Merged</c> from <c>merged_at</c> for exactly that reason.
    /// </summary>
    private static PullRequestStatus MapStatus(GitHubWirePullRequest pr) =>
        pr.Merged || pr.MergedAt is not null ? PullRequestStatus.Merged
        : string.Equals(pr.State, "closed", StringComparison.OrdinalIgnoreCase) ? PullRequestStatus.Closed
        : PullRequestStatus.Open;

    /// <summary>Our issue-state filter → GitHub's <c>state</c> query value (null = all).</summary>
    private static string MapStateFilter(GitHubIssueState? state) => state switch
    {
        GitHubIssueState.Open => "open",
        GitHubIssueState.Closed => "closed",
        _ => "all",
    };

    /// <summary>Our PR-status filter → GitHub's <c>state</c> query value (Merged folds into closed).</summary>
    private static string MapPrStateFilter(PullRequestStatus? state) => state switch
    {
        PullRequestStatus.Open => "open",
        PullRequestStatus.Closed or PullRequestStatus.Merged => "closed",
        _ => "all",
    };

    /// <summary>
    /// Maps GitHub's <c>issue</c> object to our snapshot record (comments filled separately).
    ///
    /// <para>Internal so <c>AWebhookIssueSnapshotKeepsItsCloseDecisionTest</c> can hold this mapper
    /// and <c>GitHubWebhookProcessor.MapIssue</c> to the same answer — two mappers onto one record is
    /// where a field gets added to one and forgotten in the other.</para>
    /// </summary>
    internal static GitHubIssue ToIssue(GitHubWireIssue issue) => new()
    {
        Number = issue.Number,
        Title = issue.Title,
        Body = issue.Body,
        State = string.Equals(issue.State, "closed", StringComparison.OrdinalIgnoreCase)
            ? GitHubIssueState.Closed : GitHubIssueState.Open,
        AuthorLogin = issue.User?.Login,
        Labels = (issue.Labels ?? []).Select(l => l.Name).OfType<string>().ToImmutableList(),
        Assignees = (issue.Assignees ?? []).Select(a => a.Login).OfType<string>().ToImmutableList(),
        CommentsCount = issue.Comments,
        Url = issue.HtmlUrl,
        CreatedAt = issue.CreatedAt,
        UpdatedAt = issue.UpdatedAt,
        ClosedAt = issue.ClosedAt,
        // 🚨 The RAW wire token, through a total parse: `duplicate` is a value GitHub serves that a
        // closed vendor enum could not hold (Octokit's could not — its .Value threw), and it is the
        // one reason a caller most needs. GitHubIssueStateReasons.Parse never throws.
        StateReason = GitHubIssueStateReasons.Parse(issue.StateReason),
    };

    /// <summary>Maps a GitHub issue comment to our comment record.</summary>
    private static GitHubIssueComment ToComment(GitHubWireComment c) =>
        new(c.Id, c.User?.Login, c.Body, c.CreatedAt, c.HtmlUrl);

    /// <summary>Maps a GitHub pull request to our list-row summary.</summary>
    private static GitHubPullRequestSummary ToSummary(GitHubWirePullRequest pr) =>
        new(pr.Number, pr.Title ?? "", pr.User?.Login, MapStatus(pr), pr.Draft,
            pr.Head?.Ref, pr.Base?.Ref, pr.HtmlUrl ?? "", pr.CreatedAt, pr.UpdatedAt);

    /// <summary>Combines the PR + its check/review roll-ups into the live detail record.</summary>
    private static GitHubPullRequestDetail ToDetail(
        GitHubWirePullRequest pr, string? headSha, GitHubCheckSummary checks, GitHubReviewSummary reviews) =>
        new(pr.Number, pr.Title ?? "", pr.Body, pr.User?.Login, MapStatus(pr), pr.Draft,
            pr.Head?.Ref, pr.Base?.Ref, headSha, pr.HtmlUrl ?? "",
            pr.Mergeable, pr.MergeableState, pr.Comments, checks, reviews);

    /// <summary>Rolls PR reviews up to the latest decision per reviewer (dismissed / pending don't count).</summary>
    private static GitHubReviewSummary SummarizeReviews(IReadOnlyList<GitHubWireReview> reviews)
    {
        int approved = 0, changes = 0, commented = 0;
        var latestPerReviewer = reviews
            .Where(r => r.User?.Login is { Length: > 0 })
            .GroupBy(r => r.User!.Login, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.SubmittedAt).Last());
        foreach (var r in latestPerReviewer)
        {
            var s = r.State;
            if (string.Equals(s, "APPROVED", StringComparison.OrdinalIgnoreCase)) approved++;
            else if (string.Equals(s, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase)) changes++;
            else if (string.Equals(s, "COMMENTED", StringComparison.OrdinalIgnoreCase)) commented++;
        }
        return new GitHubReviewSummary(approved, changes, commented);
    }

    /// <summary>Rolls the head commit's check runs up to totals + an overall state.</summary>
    private static GitHubCheckSummary SummarizeChecks(IReadOnlyList<GitHubWireCheckRun> runs)
    {
        int passed = 0, failed = 0, pending = 0;
        foreach (var run in runs)
        {
            if (!string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)) { pending++; continue; }
            if (run.Conclusion?.ToLowerInvariant() is "success" or "neutral" or "skipped") passed++;
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
    private IObservable<string> ResolveRefSha(string token, string owner, string repo, string commitish)
        => IsSha(commitish)
            ? GetCommit(token, owner, repo, commitish).Select(c => c.Sha)
            : GetBranchRef(token, owner, repo, commitish).Select(reference => reference.Object.Sha);

    /// <summary><c>GET /repos/{owner}/{repo}/git/commits/{sha}</c>.</summary>
    private IObservable<GitHubWireCommit> GetCommit(string token, string owner, string repo, string sha)
        => Call<GitHubWireCommit>(HttpMethod.Get, $"{RepoPath(owner, repo)}/git/commits/{GitHubRestApi.Escape(sha)}", token);

    /// <summary>
    /// <c>GET /repos/{owner}/{repo}/git/ref/heads/{branch}</c> — the EXACT-match endpoint. Octokit
    /// called the plural <c>git/refs/heads/{branch}</c>, which answers an ARRAY when the name is
    /// only a prefix of other branches (<c>feat</c> next to <c>feat-a</c>) — a body its model could
    /// not read; the singular one answers 404 there, which every caller already reads as
    /// "no such branch".
    /// </summary>
    private IObservable<GitHubWireReference> GetBranchRef(string token, string owner, string repo, string branch)
        => Call<GitHubWireReference>(HttpMethod.Get, $"{RepoPath(owner, repo)}/git/ref/heads/{GitHubRestApi.EscapePath(branch)}", token);

    /// <summary><c>POST /repos/{owner}/{repo}/git/refs</c> creating <c>refs/heads/{branch}</c> at <paramref name="sha"/>.</summary>
    private IObservable<GitHubWireReference> CreateRef(string token, string owner, string repo, string branch, string sha)
        => Call<GitHubWireReference>(HttpMethod.Post, $"{RepoPath(owner, repo)}/git/refs", token,
            new GitHubWireNewReference($"refs/heads/{branch}", sha));

    internal sealed record HeadInfo(string? CommitSha, bool RefExists, IReadOnlyList<(string Path, string Sha)> ExistingBlobs)
    {
        /// <summary>
        /// Whether <see cref="ExistingBlobs"/> is the WHOLE tree at <see cref="CommitSha"/>. False
        /// when GitHub truncated the recursive-tree response (issue #3589) — the call still returns
        /// HTTP 200, so nothing else distinguishes a partial tree from a small repository. A
        /// head with no commit at all is complete (it genuinely carries zero blobs), which is why
        /// the default is <c>true</c> and only <see cref="TreeOf"/> ever sets it false.
        /// </summary>
        public bool ListingIsComplete { get; init; } = true;
    }

    /// <summary>
    /// The missing-branch base policy: a new branch on a NON-empty repo builds on the DEFAULT
    /// branch head — its <see cref="HeadInfo.CommitSha"/> becomes the commit's parent and its
    /// <see cref="HeadInfo.ExistingBlobs"/> the preserved tree — while <c>RefExists</c> is forced
    /// false so the push CREATES the target ref. Without this the commit was parent-less: an
    /// ORPHAN branch with no common ancestor, impossible to PR/merge into the default branch.
    /// </summary>
    internal static HeadInfo AsNewBranchBase(HeadInfo defaultHead) => defaultHead with { RefExists = false };

    /// <summary>The repo-existence/creation REST step exposed for <see cref="GitProtocolRepoClient"/> —
    /// the one push ingredient git itself cannot do. Emits true when the repo was created.</summary>
    internal IObservable<bool> EnsureRepoExists(string repositoryUrl, string accessToken, bool createIfMissing)
    {
        var (owner, repo) = ParseRepoUrl(repositoryUrl);
        return EnsureRepo(accessToken, owner, repo, createIfMissing);
    }

    /// <summary>Ensures the repo exists; creates it private (under the user or the org) when missing.</summary>
    private IObservable<bool> EnsureRepo(string token, string owner, string repo, bool createIfMissing)
        => Call<GitHubWireRepository>(HttpMethod.Get, RepoPath(owner, repo), token)
            .Select(_ => false)
            .Catch<bool, GitHubNotFoundException>(_ =>
            {
                if (!createIfMissing)
                    return Observable.Throw<bool>(new InvalidOperationException(
                        $"Repository {owner}/{repo} does not exist and auto-create is disabled."));
                var newRepo = new GitHubWireNewRepository(repo, Private: true, AutoInit: false);
                logger?.LogInformation("Creating private GitHub repo {Owner}/{Repo}.", owner, repo);
                return Call<GitHubWireUser>(HttpMethod.Get, "user", token)
                    .SelectMany(me => Call<GitHubWireRepository>(HttpMethod.Post,
                        string.Equals(me.Login, owner, StringComparison.OrdinalIgnoreCase)
                            ? "user/repos"
                            : $"orgs/{GitHubRestApi.Escape(owner)}/repos",
                        token, newRepo))
                    .Select(_ => true);
            });

    /// <summary>Reads the branch head commit + its recursive blob list. Tolerates an empty repo (no ref yet).</summary>
    private IObservable<HeadInfo> ReadHead(string token, string owner, string repo, string branch)
        => GetBranchRef(token, owner, repo, branch)
            .SelectMany(reference => GetCommit(token, owner, repo, reference.Object.Sha)
                .SelectMany(commit => TreeOf(token, owner, repo, commit)))
            // No commit to build on → no head, no existing blobs (the export then makes a first,
            // PARENT-LESS commit that creates the branch ref). GitHub signals "no commit" TWO ways:
            // a missing branch on a non-empty repo is 404 (GitHubNotFoundException), but a brand-new repo
            // with ZERO commits has an unborn HEAD and returns 409 "Git Repository is empty." for
            // ref/commit lookups. The original catch only handled 404, so the very first "sync back"
            // to a freshly-created empty repo failed with that 409.
            .Catch<HeadInfo, GitHubApiException>(ex =>
                IsMissingOrEmptyRepo(ex)
                    ? Observable.Return(new HeadInfo(null, false, Array.Empty<(string, string)>()))
                    : Observable.Throw<HeadInfo>(ex));

    /// <summary>True when a GitHub error means "this branch has no commit to build on": a missing
    /// branch (404 <see cref="GitHubNotFoundException"/>) or a brand-new repo whose HEAD is unborn —
    /// GitHub returns 409 "Git Repository is empty." for ref/commit lookups until the first commit lands.</summary>
    internal static bool IsMissingOrEmptyRepo(GitHubApiException ex)
        => ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict;

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
        string token, string owner, string repo, string branch, string prefix, HeadInfo head)
        => head.RefExists
            ? Observable.Return(head)
            : IsRepoEmpty(token, owner, repo).SelectMany(empty => empty
                ? api.SendRaw(HttpMethod.Put,
                        $"{RepoPath(owner, repo)}/contents/{GitHubRestApi.EscapePath(prefix + ".gitkeep")}", token,
                        new GitHubWireCreateFile("Initialize repository (MeshWeaver sync)",
                            Convert.ToBase64String(Encoding.UTF8.GetBytes("Created by MeshWeaver to initialize this repository.\n")),
                            branch))
                    .SelectMany(_ => ReadHead(token, owner, repo, branch))
                : ReadDefaultBranchHead(token, owner, repo, fallback: head));

    /// <summary>
    /// The DEFAULT branch's head with <c>RefExists=false</c> — the base for creating a new branch
    /// with real history (the caller still CREATES the target ref; the commit just parents on the
    /// default head and preserves its tree). Falls back to <paramref name="fallback"/> when the
    /// default branch cannot be resolved (races with repo initialization).
    /// </summary>
    private IObservable<HeadInfo> ReadDefaultBranchHead(
        string token, string owner, string repo, HeadInfo fallback)
        => Call<GitHubWireRepository>(HttpMethod.Get, RepoPath(owner, repo), token)
            .SelectMany(r => string.IsNullOrEmpty(r.DefaultBranch)
                ? Observable.Return(fallback)
                : ReadHead(token, owner, repo, r.DefaultBranch))
            .Select(AsNewBranchBase)
            .Catch<HeadInfo, GitHubApiException>(ex => IsMissingOrEmptyRepo(ex)
                ? Observable.Return(fallback)
                : Observable.Throw<HeadInfo>(ex));

    /// <summary>True when the repo has no commits at all (a freshly-created repo): GitHub lists zero
    /// branches for an empty repo, and some endpoints 409 "Git Repository is empty.".</summary>
    private IObservable<bool> IsRepoEmpty(string token, string owner, string repo)
        // ONE page of one branch answers the question (empty ⇔ no branches); Octokit listed every
        // page to reach the same count.
        => Call<ImmutableList<GitHubWireBranch>>(HttpMethod.Get, RepoPath(owner, repo) + "/branches?per_page=1", token)
            .Select(branches => branches.Count == 0)
            .Catch<bool, GitHubApiException>(ex => IsMissingOrEmptyRepo(ex)
                ? Observable.Return(true)
                : Observable.Throw<bool>(ex));

    /// <summary>
    /// Resolves a commitish (a branch name OR a commit SHA) to its commit + recursive
    /// blob list. Unlike <see cref="ReadHead"/> this does NOT swallow NotFound — a
    /// re-import at a non-existent commit/branch must surface a clear error.
    /// </summary>
    private IObservable<HeadInfo> ResolveCommitish(string token, string owner, string repo, string commitish)
        => IsSha(commitish)
            ? GetCommit(token, owner, repo, commitish)
                .SelectMany(commit => TreeOf(token, owner, repo, commit))
            : GetBranchRef(token, owner, repo, commitish)
                .SelectMany(reference => GetCommit(token, owner, repo, reference.Object.Sha)
                    .SelectMany(commit => TreeOf(token, owner, repo, commit)));

    /// <summary>
    /// The commit's recursive blob list — and, crucially, whether that list is the WHOLE tree.
    ///
    /// <para>🚨 <b>GitHub answers HTTP 200 with a PARTIAL list</b> when a recursive tree exceeds its
    /// response cap; the only signal is <c>truncated: true</c> in the body (issue #3589). Before this
    /// was read, a truncated tree flowed on as "the repository contains exactly these files", and a
    /// <c>FullReplace</c> import then deleted every mesh node whose file GitHub had simply not
    /// returned. The completeness verdict is carried rather than the failure thrown, because the
    /// blobs that WERE returned are still valid to upsert — it is only the "everything else was
    /// deleted" INFERENCE that has to be withheld. The identical read already exists next door for
    /// the compare endpoint (<see cref="CompareFileCap"/> → null → full import).</para>
    /// </summary>
    private IObservable<HeadInfo> TreeOf(string token, string owner, string repo, GitHubWireCommit commit)
        => Call<GitHubWireTree>(HttpMethod.Get,
                $"{RepoPath(owner, repo)}/git/trees/{GitHubRestApi.Escape(commit.Tree.Sha)}?recursive=1", token)
            .Select(tree =>
            {
                if (tree.Truncated)
                    logger?.LogWarning(
                        "[GitSync] {Owner}/{Repo}@{Sha}: GitHub TRUNCATED the recursive tree — the blob list is "
                        + "PARTIAL. Treating the listing as incomplete so no import can read an omitted file as "
                        + "a deletion.", owner, repo, commit.Sha);
                return new HeadInfo(
                    commit.Sha, true,
                    (tree.Tree ?? [])
                        .Where(i => string.Equals(i.Type, "blob", StringComparison.OrdinalIgnoreCase))
                        .Select(i => (i.Path, i.Sha))
                        .ToArray())
                { ListingIsComplete = !tree.Truncated };
            });

    /// <summary>A 7–40 char all-hex token is treated as a commit SHA; anything else as a branch name.</summary>
    private static bool IsSha(string s) =>
        s.Length is >= 7 and <= 40 && s.All(Uri.IsHexDigit);

    private IObservable<IReadOnlyList<(string Path, string Sha)>> CreateBlobs(
        string token, string owner, string repo, IReadOnlyList<RepoFile> files, string prefix)
    {
        if (files.Count == 0)
            return Observable.Return((IReadOnlyList<(string, string)>)Array.Empty<(string, string)>());
        return files
            .Select(f => Call<GitHubWireCreated>(HttpMethod.Post, RepoPath(owner, repo) + "/git/blobs", token,
                    new GitHubWireNewBlob(f.Content, "utf-8"))
                .Select(blob => (Path: prefix + f.Path, blob.Sha)))
            .MergeBounded(8)
            .ToList()
            .Select(list => (IReadOnlyList<(string, string)>)list);
    }

    /// <summary>Moves (<c>PATCH git/refs/heads/{branch}</c>, never forced) or creates the branch ref.</summary>
    private IObservable<GitHubWireReference> UpdateRef(
        string token, string owner, string repo, string branch, string commitSha, bool refExists)
        => refExists
            ? Call<GitHubWireReference>(HttpMethod.Patch,
                $"{RepoPath(owner, repo)}/git/refs/heads/{GitHubRestApi.EscapePath(branch)}", token,
                new GitHubWireReferenceUpdate(commitSha, Force: false))
            : CreateRef(token, owner, repo, branch, commitSha);

    /// <summary>
    /// Builds a <see cref="RepoFile"/> from a fetched blob, PRESERVING BINARY BYTES. A base64 blob is
    /// decoded to raw bytes; if those bytes are valid UTF-8 the file is a text <see cref="RepoFile"/>
    /// (so node/markdown parsing is unchanged), otherwise the raw bytes are carried in
    /// <see cref="RepoFile.Binary"/>. This is the fix for the corruption that repeatedly nuked the
    /// course videos: the old <c>DecodeBlob</c> UTF-8-decoded a base64 <c>.mp4</c>/<c>.png</c> blob
    /// into a string, mangling every non-UTF-8 byte. A non-base64 (already-utf8) blob stays text.
    /// </summary>
    private static RepoFile ToRepoFile(string path, GitHubWireBlob blob)
    {
        if (!string.Equals(blob.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
            return new RepoFile(path, blob.Content);
        // Shared strict-UTF-8 text/binary classification (RepoFileCodec) — one rule per transport.
        return RepoFileCodec.FromBytes(path, Convert.FromBase64String(blob.Content));
    }

    private static string NormalizePrefix(string? subdirectory)
    {
        var s = subdirectory?.Trim().Trim('/');
        return string.IsNullOrEmpty(s) ? "" : s + "/";
    }
}
