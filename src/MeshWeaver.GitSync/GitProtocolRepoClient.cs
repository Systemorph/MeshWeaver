using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The production <see cref="IGitHubRepoClient"/>: BULK transfer (<see cref="Push"/> /
/// <see cref="Fetch(string,string,string?,string)"/>) over the native <b>git protocol</b> via
/// <see cref="GitCli"/>; every other operation (refs, PRs, issues — cheap single calls) delegated
/// to <see cref="OctokitGitHubRepoClient"/>.
///
/// <para><b>Why:</b> the REST Git Data API pays ONE request PER FILE — a blob read per file on
/// fetch, a blob create per file on push — so a single sync of a large content repo (a course with
/// thousands of files) burned most of a GitHub App installation's 5,000 req/h budget and rate-limited
/// every other sync for the hour. Git smart-HTTP transfer (clone / fetch / push with the
/// installation token) does not count against the REST rate limit at all; what remains on REST is
/// repo creation, ref lookups and the PR/issue surface — a handful of calls per operation.</para>
///
/// <para>Semantics are byte-identical to the Octokit implementation and pinned by the shared
/// contract tests: mirror-within-subdirectory (files outside the prefix untouched, removed files
/// deleted), a missing branch on a non-empty repo based on the default-branch head (never an
/// orphan), an empty repo initialized by the first pushed commit (the Contents-API
/// <c>.gitkeep</c> dance is obsolete — the git protocol can create the first commit directly),
/// and strict UTF-8 text/binary classification (<see cref="RepoFileCodec"/>). Additionally the
/// git protocol writes <see cref="RepoFile.Bytes"/>, so BINARY files push losslessly — the REST
/// path could only create UTF-8 blobs.</para>
///
/// <para>Reactive end-to-end: every git invocation is a blocking Process leaf bridged through
/// <see cref="IIoPool"/> by <see cref="GitCli"/>; worktree reads/writes run on the
/// <see cref="IoPoolNames.FileSystem"/> pool. All methods return COLD observables. Each operation
/// works in its own unique temp clone, removed on termination.</para>
/// </summary>
public sealed class GitProtocolRepoClient(
    OctokitGitHubRepoClient octokit,
    GitCli git,
    IoPoolRegistry ioPools,
    ILogger<GitProtocolRepoClient>? logger = null) : IGitHubRepoClient
{
    private IIoPool FileSystem => ioPools.Get(IoPoolNames.FileSystem);

    // ══════════════════════════════════════════════════════════════════════════
    //  Bulk transfer — the git protocol
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mirrors the request's files into the repo as a single commit over the git protocol:
    /// shallow-clone, materialize the mirror in the worktree, commit as the requested author,
    /// push. Repo auto-create (a GitHub REST call — the one thing git cannot do) stays on the
    /// Octokit client.
    /// </summary>
    public IObservable<GitHubPushResult> Push(GitHubPushRequest request)
    {
        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch.Trim();
        var prefix = NormalizePrefix(request.Subdirectory);
        return EnsureRepo(request)
            .SelectMany(repoCreated => WithTempDir(tmp =>
                Clone(request.RepositoryUrl, request.AccessToken, tmp)
                    .SelectMany(_ => CheckoutTargetBranch(tmp, branch, request))
                    .SelectMany(refExists => TrackedFilesUnder(tmp, prefix)
                        .SelectMany(existing =>
                        {
                            var exportPaths = request.Files
                                .Select(f => prefix + f.Path)
                                .ToHashSet(StringComparer.Ordinal);
                            var deleted = existing.Count(p => !exportPaths.Contains(p));
                            return MirrorWorktree(tmp, prefix, request.Files)
                                .SelectMany(_ => Commit(tmp, request))
                                .SelectMany(_ => Expect(git.Run(tmp,
                                    [.. GitCredentials.AuthArgs(request.AccessToken),
                                        "push", "-q", "origin", $"HEAD:refs/heads/{branch}"],
                                    GitCredentials.AuthEnv(request.AccessToken))))
                                .SelectMany(_ => Expect(git.Run(tmp, ["rev-parse", "HEAD"])))
                                .Select(sha => new GitHubPushResult(
                                    sha.StdOut.Trim(), request.RepositoryUrl,
                                    request.Files.Count, deleted, repoCreated));
                        }))));
    }

    /// <summary>
    /// Snapshot at a commitish over the git protocol: <c>init + fetch --depth 1 + checkout</c>
    /// (one negotiated pack transfer instead of a REST call per file), then read the worktree.
    /// A SHORT commit SHA cannot travel over the wire protocol (only refs and full SHAs can) —
    /// that one case delegates to the REST client.
    /// </summary>
    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken)
        => Fetch(repositoryUrl, commitish, subdirectory, accessToken, _ => true);

    /// <summary>
    /// Filtered fetch. The git protocol transfers the (shallow) pack in one exchange, so the
    /// filter is applied while reading the worktree — same REST cost (zero) either way. The
    /// interface's contract (only matching files in the snapshot) is unchanged.
    /// </summary>
    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken,
        Func<string, bool> pathFilter)
    {
        ArgumentNullException.ThrowIfNull(pathFilter);
        var commitRef = string.IsNullOrWhiteSpace(commitish) ? "main" : commitish.Trim();
        if (IsShortSha(commitRef))
            // Wire fetch needs a ref or a FULL SHA; a short SHA resolves only through REST.
            return octokit.Fetch(repositoryUrl, commitRef, subdirectory, accessToken, pathFilter);
        var prefix = NormalizePrefix(subdirectory);
        return WithTempDir(tmp =>
            Expect(git.Run(tmp, ["init", "-q"]))
                .SelectMany(_ => Expect(git.Run(tmp, ["remote", "add", "origin", repositoryUrl])))
                .SelectMany(_ => Expect(git.Run(tmp,
                    [.. GitCredentials.AuthArgs(accessToken), "fetch", "-q", "--depth", "1", "origin", commitRef],
                    GitCredentials.AuthEnv(accessToken))))
                .SelectMany(_ => Expect(git.Run(tmp, ["checkout", "-q", "--detach", "FETCH_HEAD"])))
                .SelectMany(_ => Expect(git.Run(tmp, ["rev-parse", "FETCH_HEAD"])))
                .SelectMany(sha => ReadWorktree(tmp, prefix, pathFilter)
                    .Select(files => new RepoSnapshot(sha.StdOut.Trim(), files))));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Everything else — cheap single REST calls, delegated to Octokit
    // ══════════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public IObservable<string> GetHeadSha(string repositoryUrl, string commitish, string accessToken)
        => octokit.GetHeadSha(repositoryUrl, commitish, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request)
        => octokit.CreateBranch(request);

    /// <inheritdoc />
    public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request)
        => octokit.OpenPullRequest(request);

    /// <inheritdoc />
    public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
        string repositoryUrl, int number, string accessToken)
        => octokit.GetPullRequestStatus(repositoryUrl, number, accessToken);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
        string repositoryUrl, GitHubIssueState? state, string accessToken)
        => octokit.ListIssues(repositoryUrl, state, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken)
        => octokit.GetIssue(repositoryUrl, number, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request)
        => octokit.CreateIssue(request);

    /// <inheritdoc />
    public IObservable<GitHubIssueComment> CommentIssue(
        string repositoryUrl, int number, string body, string accessToken)
        => octokit.CommentIssue(repositoryUrl, number, body, accessToken);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
        string repositoryUrl, PullRequestStatus? state, string accessToken)
        => octokit.ListPullRequests(repositoryUrl, state, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
        string repositoryUrl, int number, string accessToken)
        => octokit.GetPullRequestDetail(repositoryUrl, number, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubIssueComment> CommentPullRequest(
        string repositoryUrl, int number, string body, string accessToken)
        => octokit.CommentPullRequest(repositoryUrl, number, body, accessToken);

    /// <inheritdoc />
    public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request)
        => octokit.MergePullRequest(request);

    // ── push internals ───────────────────────────────────────────────────────

    /// <summary>Repo existence/creation is REST-only (git cannot create a GitHub repo) — and only
    /// meaningful for a GitHub remote; a local/file remote (tests) is taken as existing.</summary>
    private IObservable<bool> EnsureRepo(GitHubPushRequest request)
        => IsGitHubUrl(request.RepositoryUrl)
            ? octokit.EnsureRepoExists(
                request.RepositoryUrl, request.AccessToken, request.CreatePrivateIfMissing)
            : Observable.Return(false);

    /// <summary>
    /// Shallow clone of the remote's DEFAULT branch. Tolerates an EMPTY repo (git exits 0 with an
    /// unborn HEAD — the first commit then initializes it; no Contents-API seeding needed).
    /// </summary>
    private IObservable<GitCommandResult> Clone(string url, string token, string tmp)
        => Expect(git.Run(tmp,
            [.. GitCredentials.AuthArgs(token), "clone", "-q", "--depth", "1", url, "."],
            GitCredentials.AuthEnv(token)));

    /// <summary>
    /// Puts the worktree on the TARGET branch and reports whether it existed on the remote:
    /// <list type="bullet">
    ///   <item>exists → fetch it (depth 1) and check it out at the remote head;</item>
    ///   <item>missing + auto-create + a non-empty clone → a new branch BASED ON THE DEFAULT
    ///     HEAD (never an orphan — the same policy the REST path pinned in NewBranchBaseTest);</item>
    ///   <item>missing + auto-create + an EMPTY repo (unborn HEAD) → rename the unborn branch,
    ///     so the first commit creates it;</item>
    ///   <item>missing + auto-create disabled → error.</item>
    /// </list>
    /// </summary>
    private IObservable<bool> CheckoutTargetBranch(string tmp, string branch, GitHubPushRequest request)
        => Expect(git.Run(tmp,
                [.. GitCredentials.AuthArgs(request.AccessToken), "ls-remote", "--heads", "origin", branch],
                GitCredentials.AuthEnv(request.AccessToken)))
            .SelectMany(remote =>
            {
                var refExists = remote.StdOut.Trim().Length > 0;
                if (refExists)
                    return Expect(git.Run(tmp,
                            [.. GitCredentials.AuthArgs(request.AccessToken),
                                "fetch", "-q", "--depth", "1", "origin", branch],
                            GitCredentials.AuthEnv(request.AccessToken)))
                        .SelectMany(_ => Expect(git.Run(tmp, ["checkout", "-q", "-B", branch, "FETCH_HEAD"])))
                        .Select(_ => true);
                if (!request.CreateBranchIfMissing)
                    return Observable.Throw<bool>(new InvalidOperationException(
                        $"Branch '{branch}' does not exist and branch auto-create is disabled."));
                // Unborn HEAD (empty repo) cannot `checkout -B`; renaming the unborn ref suffices.
                return git.Run(tmp, ["rev-parse", "--verify", "--quiet", "HEAD"])
                    .SelectMany(head => head.Ok
                        ? Expect(git.Run(tmp, ["checkout", "-q", "-B", branch])).Select(_ => false)
                        : Expect(git.Run(tmp, ["symbolic-ref", "HEAD", $"refs/heads/{branch}"]))
                            .Select(_ => false));
            });

    /// <summary>The tracked repo-relative paths under <paramref name="prefix"/> — the deletion
    /// candidates of the mirror. Empty on an unborn HEAD (nothing tracked yet).</summary>
    private IObservable<IReadOnlyList<string>> TrackedFilesUnder(string tmp, string prefix)
        => Expect(git.Run(tmp, prefix.Length == 0
                ? ["ls-files"]
                : ["ls-files", "--", prefix]))
            .Select(r => (IReadOnlyList<string>)r.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// Materializes the mirror in the worktree: everything under <paramref name="prefix"/> is
    /// removed (whole worktree minus <c>.git</c> when the prefix is empty — the export fully
    /// defines the tree), then every file is written from its BYTES (binary-safe — the REST path
    /// could only create UTF-8 blobs). <c>git add -A</c> turns the difference into the commit.
    /// </summary>
    private IObservable<System.Reactive.Unit> MirrorWorktree(
        string tmp, string prefix, IReadOnlyList<RepoFile> files)
        => FileSystem.InvokeBlocking<System.Reactive.Unit>(_ =>
        {
            var root = Path.GetFullPath(tmp);
            var target = prefix.Length == 0 ? root : Path.Combine(root, prefix.TrimEnd('/'));
            if (Directory.Exists(target))
            {
                if (prefix.Length == 0)
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(root))
                    {
                        if (string.Equals(Path.GetFileName(entry), ".git", StringComparison.Ordinal))
                            continue;
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else File.Delete(entry);
                    }
                }
                else
                {
                    Directory.Delete(target, recursive: true);
                }
            }
            foreach (var file in files)
            {
                var full = Path.GetFullPath(Path.Combine(root, prefix + file.Path));
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Path '{file.Path}' escapes the repository.");
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, file.Bytes);
            }
            return System.Reactive.Unit.Default;
        });

    /// <summary>Stages the mirror and commits as the requested author. <c>--allow-empty</c> keeps
    /// parity with the REST path, which records a sync commit even when nothing changed.</summary>
    private IObservable<GitCommandResult> Commit(string tmp, GitHubPushRequest request)
        => Expect(git.Run(tmp, ["add", "-A"]))
            .SelectMany(_ => Expect(git.Run(tmp,
            [
                "-c", $"user.name={request.AuthorName}",
                "-c", $"user.email={request.AuthorEmail}",
                "-c", "commit.gpgsign=false",
                "commit", "-q", "--allow-empty", "-m", request.CommitMessage,
            ])));

    // ── fetch internals ──────────────────────────────────────────────────────

    /// <summary>Reads the checked-out worktree into subdirectory-relative <see cref="RepoFile"/>s,
    /// classifying text vs binary exactly like the blob decode (<see cref="RepoFileCodec"/>).</summary>
    private IObservable<IReadOnlyList<RepoFile>> ReadWorktree(
        string tmp, string prefix, Func<string, bool> pathFilter)
        => FileSystem.InvokeBlocking(_ =>
        {
            var root = Path.GetFullPath(tmp);
            var files = new List<RepoFile>();
            foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
                if (rel.StartsWith(".git/", StringComparison.Ordinal))
                    continue;
                if (prefix.Length > 0 && !rel.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var subRel = prefix.Length == 0 ? rel : rel[prefix.Length..];
                if (!pathFilter(subRel))
                    continue;
                files.Add(RepoFileCodec.FromBytes(subRel, File.ReadAllBytes(full)));
            }
            return (IReadOnlyList<RepoFile>)files;
        });

    // ── shared plumbing ──────────────────────────────────────────────────────

    /// <summary>
    /// A unique temp directory around a cold pipeline: created (on the FileSystem pool) before
    /// the work subscribes, removed (best effort, on the pool) when it terminates — success,
    /// error, or unsubscribe alike. Each operation owns its own directory, so concurrent syncs
    /// never collide.
    /// </summary>
    private IObservable<T> WithTempDir<T>(Func<string, IObservable<T>> work)
        => Observable.Defer(() =>
        {
            var tmp = Path.Combine(Path.GetTempPath(), "mw-gitsync-" + Guid.NewGuid().ToString("N"));
            return FileSystem.InvokeBlocking(_ =>
                {
                    Directory.CreateDirectory(tmp);
                    return tmp;
                })
                .SelectMany(work)
                .Finally(() => FileSystem.InvokeBlocking<System.Reactive.Unit>(_ =>
                    {
                        if (Directory.Exists(tmp))
                            Directory.Delete(tmp, recursive: true);
                        return System.Reactive.Unit.Default;
                    })
                    .Subscribe(
                        _ => { },
                        ex => logger?.LogWarning(ex, "Temp clone cleanup failed for {Dir}.", tmp)));
        });

    /// <summary>Passes Ok results through; converts a non-zero git exit into a typed error.</summary>
    private static IObservable<GitCommandResult> Expect(IObservable<GitCommandResult> op)
        => op.SelectMany(r => r.Ok
            ? Observable.Return(r)
            : Observable.Throw<GitCommandResult>(new InvalidOperationException(
                $"git failed (exit {r.ExitCode}): {r.Message}")));

    /// <summary>True for a GitHub http(s) remote — where the REST surface (repo create) applies.</summary>
    private static bool IsGitHubUrl(string url)
        => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>An abbreviated commit SHA (7–39 hex chars) — resolvable via REST only; the wire
    /// protocol fetches refs and FULL 40-char SHAs.</summary>
    private static bool IsShortSha(string s)
        => s.Length is >= 7 and < 40 && s.All(Uri.IsHexDigit);

    private static string NormalizePrefix(string? subdirectory)
    {
        var s = subdirectory?.Trim().Trim('/');
        return string.IsNullOrEmpty(s) ? "" : s + "/";
    }
}
