using System.Collections.Immutable;
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
        => Wire(repositoryUrl, commitish, subdirectory, accessToken, _ => true, narrow: false);

    /// <summary>
    /// Filtered fetch — and, unlike the unfiltered one above, it transfers ONLY the blobs the
    /// filter keeps, which is the interface's stated contract
    /// (<see cref="IGitHubRepoClient.Fetch(string,string,string?,string,Func{string,bool})"/>:
    /// <i>"every package's <c>*/index.json</c> manifest without pulling the rest of the repo"</i>).
    ///
    /// <para>🚨 <b>It used to fetch everything and filter while reading the worktree</b>, on the
    /// reasoning that the git protocol transfers its pack in one exchange so the filter costs the
    /// same REST calls (zero) either way. That is true of REST CALLS and false of BYTES, and bytes
    /// are what the caller waits for. Measured against <c>MeshWeaver.Plugins</c> on 2026-09-16:
    /// the whole-content transfer is <b>47.8 MB / 13 s</b>, while the 143 manifest files the
    /// plugin catalog's listing actually parses are <b>0.8 MB</b> — 1.7% of it. That 13 s was the
    /// dominant term in <c>GET /api/plugins</c>'s 12–19 s time to first byte and the reason the
    /// consumer's 30 s attempt budget was exceeded ~60×/day (#4222). The narrow path below
    /// measures <b>3.3 s / 1.3 MB</b> for the identical answer at the identical commit.</para>
    ///
    /// <para><b>How.</b> <c>--filter=blob:none</c> fetches commits and TREES only (a blobless
    /// partial clone), so the whole path list is known before a single file's bytes move;
    /// <c>ls-tree</c> then reads that list for free, the caller's predicate selects from it, and a
    /// <c>--no-cone</c> sparse-checkout of exactly those paths makes the checkout materialise them
    /// in ONE batched lazy fetch. Nothing else is ever transferred. A filter that matches nothing
    /// skips the checkout entirely and moves no blobs at all.</para>
    ///
    /// <para><b>Fallback, and why it is not fault-hiding.</b> Partial clone is a server capability
    /// (<c>uploadpack.allowFilter</c>); a remote that refuses it fails the fetch. That one case
    /// retries the plain shallow fetch — the SAME snapshot the caller would have got before this
    /// change, just with more bytes moved — and says so in the log. A failure of THAT fetch
    /// propagates; nothing is swallowed.</para>
    /// </summary>
    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken,
        Func<string, bool> pathFilter)
    {
        ArgumentNullException.ThrowIfNull(pathFilter);
        return Wire(repositoryUrl, commitish, subdirectory, accessToken, pathFilter, narrow: true);
    }

    /// <summary>
    /// The shared shape of both fetches: one temp clone, the short-SHA REST fallback around it.
    /// <paramref name="narrow"/> picks whether the blobs are selected before or after transfer.
    /// </summary>
    private IObservable<RepoSnapshot> Wire(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken,
        Func<string, bool> pathFilter, bool narrow)
    {
        var commitRef = string.IsNullOrWhiteSpace(commitish) ? "main" : commitish.Trim();
        var prefix = NormalizePrefix(subdirectory);
        var wire = WithTempDir(tmp => narrow
            ? NarrowSnapshot(tmp, repositoryUrl, commitRef, accessToken, prefix, pathFilter)
            : WholeSnapshot(tmp, repositoryUrl, commitRef, accessToken, prefix, pathFilter));
        // A hex-looking name is tried over the wire FIRST — "deadbee" may be a legitimate
        // branch/tag, and the whole point of this client is to avoid per-file REST. Only when
        // the wire fetch fails AND the commitish is an ABBREVIATED SHA (the one commitish the
        // protocol cannot serve — upload-pack wants refs or full SHAs) does REST resolve it.
        return IsShortSha(commitRef)
            ? wire.Catch((Exception ex) =>
            {
                logger?.LogInformation(
                    "Wire fetch of '{Commitish}' failed ({Error}); resolving the abbreviated SHA via REST.",
                    commitRef, ex.Message);
                return octokit.Fetch(repositoryUrl, commitRef, subdirectory, accessToken, pathFilter);
            })
            : wire;
    }

    /// <summary>Today's transfer: the whole (shallow) pack, then read the worktree through the
    /// filter. What the UNFILTERED fetch wants — every file is the answer, so selecting paths
    /// before the transfer would only add round trips.</summary>
    private IObservable<RepoSnapshot> WholeSnapshot(
        string tmp, string repositoryUrl, string commitRef, string accessToken,
        string prefix, Func<string, bool> pathFilter)
        => InitRemote(tmp, repositoryUrl)
            .SelectMany(_ => Expect(git.Run(tmp,
                [.. GitCredentials.AuthArgs(accessToken), "fetch", "-q", "--depth", "1", "origin", commitRef],
                GitCredentials.AuthEnv(accessToken))))
            .SelectMany(_ => CheckoutEverything(tmp, accessToken))
            .SelectMany(_ => Snapshot(tmp, prefix, pathFilter));

    /// <summary>
    /// The narrow transfer: trees first, then exactly the blobs the filter keeps. See the
    /// <see cref="Fetch(string,string,string?,string,Func{string,bool})"/> doc for the measurement
    /// that motivates it.
    /// </summary>
    private IObservable<RepoSnapshot> NarrowSnapshot(
        string tmp, string repositoryUrl, string commitRef, string accessToken,
        string prefix, Func<string, bool> pathFilter)
        => InitRemote(tmp, repositoryUrl)
            .SelectMany(_ => Expect(git.Run(tmp,
                    [.. GitCredentials.AuthArgs(accessToken),
                        "fetch", "-q", "--depth", "1", "--filter=blob:none", "origin", commitRef],
                    GitCredentials.AuthEnv(accessToken)))
                .Select(result =>
                {
                    // 🚨 A remote that does not serve partial clones does NOT fail the fetch: git
                    // prints "filtering not recognized by server, ignoring" and exits 0, having
                    // transferred everything. The answer is still correct — the sparse checkout
                    // below just finds the blobs already local — but the byte saving is gone, and
                    // an unannounced loss of it is exactly how this latency came back last time.
                    if (FilterWasIgnored(result))
                        logger?.LogInformation(
                            "{Repo} does not serve partial clones (uploadpack.allowFilter), so the "
                            + "filtered fetch still transferred the whole repository.", repositoryUrl);
                    return true;
                })
                .Catch((Exception ex) =>
                {
                    // 🚨 An UNSUBSCRIBE is not a refusal. The pool cancels its token when nobody is
                    // listening any more, and retrying there would start a SECOND — and this time
                    // whole-repository — fetch for an answer no one will read.
                    if (ex is OperationCanceledException)
                        return Observable.Throw<bool>(ex);
                    // Capability negotiation, not fault suppression: the remote does not serve
                    // partial clones, so the ONLY thing lost is the byte saving. The retry below
                    // produces the identical snapshot, and its own failure propagates.
                    logger?.LogInformation(
                        "Partial (blob:none) fetch of {Repo} was refused ({Error}); falling back to a "
                        + "full shallow fetch — same answer, more bytes.", repositoryUrl, ex.Message);
                    return Expect(git.Run(tmp,
                            [.. GitCredentials.AuthArgs(accessToken),
                                "fetch", "-q", "--depth", "1", "origin", commitRef],
                            GitCredentials.AuthEnv(accessToken)))
                        .Select(_ => false);
                }))
            .SelectMany(partial => partial
                ? MaterializeMatching(tmp, accessToken, prefix, pathFilter)
                : CheckoutEverything(tmp, accessToken))
            .SelectMany(_ => Snapshot(tmp, prefix, pathFilter));

    /// <summary>
    /// Selects the matching paths off the (blobless) tree and checks out ONLY those, so the lazy
    /// blob fetch moves nothing else. Falls back to a whole checkout when the selection cannot be
    /// expressed as sparse patterns — see <see cref="MaxNarrowPaths"/> and
    /// <see cref="HasPatternMetacharacter"/>; that is still correct, just not narrow.
    /// </summary>
    private IObservable<System.Reactive.Unit> MaterializeMatching(
        string tmp, string accessToken, string prefix, Func<string, bool> pathFilter)
        // -z so paths arrive raw: ls-tree QUOTES anything unusual otherwise, and a quoted path
        // would be selected, pattern-matched and read under a name the worktree does not have.
        => Expect(git.Run(tmp, ["ls-tree", "-r", "--name-only", "-z", "FETCH_HEAD"]))
            .SelectMany(tree =>
            {
                var total = CountPaths(tree.StdOut);
                var wanted = WantedPatterns(tree.StdOut, prefix, pathFilter);
                if (wanted is null)
                {
                    // Not expressible as sparse patterns — check everything out. The read still
                    // applies the filter, so the ANSWER is identical either way.
                    logger?.LogDebug(
                        "Narrow fetch: the selection over {Total} path(s) is not expressible as "
                        + "sparse patterns; checking out the whole tree.", total);
                    return CheckoutEverything(tmp, accessToken);
                }
                logger?.LogDebug(
                    "Narrow fetch: {Wanted} of {Total} path(s) selected.", wanted.Count, total);
                if (wanted.Count == 0)
                    // The filter matches nothing at this commit: no checkout, no blob, no bytes.
                    return Observable.Return(System.Reactive.Unit.Default);
                return Expect(git.Run(tmp, ["sparse-checkout", "set", "--no-cone", "--", .. wanted]))
                    .SelectMany(_ => CheckoutEverything(tmp, accessToken));
            });

    /// <summary>The checkout. Carries the credentials because in a partial clone it is the command
    /// that lazily fetches the missing blobs — an unauthenticated checkout of a private repo would
    /// fail there rather than at the fetch.</summary>
    private IObservable<System.Reactive.Unit> CheckoutEverything(string tmp, string accessToken)
        => Expect(git.Run(tmp,
                [.. GitCredentials.AuthArgs(accessToken), "checkout", "-q", "--detach", "FETCH_HEAD"],
                GitCredentials.AuthEnv(accessToken)))
            .Select(_ => System.Reactive.Unit.Default);

    private IObservable<GitCommandResult> InitRemote(string tmp, string repositoryUrl)
        => Expect(git.Run(tmp, ["init", "-q"]))
            .SelectMany(_ => Expect(git.Run(tmp, ["remote", "add", "origin", repositoryUrl])));

    private IObservable<RepoSnapshot> Snapshot(string tmp, string prefix, Func<string, bool> pathFilter)
        => Expect(git.Run(tmp, ["rev-parse", "FETCH_HEAD"]))
            .SelectMany(sha => ReadWorktree(tmp, prefix, pathFilter)
                .Select(files => new RepoSnapshot(sha.StdOut.Trim(), files)));

    /// <summary>
    /// The sparse-checkout patterns for the paths the filter keeps — repo-root-relative and
    /// leading-slash anchored, which is what <c>--no-cone</c> matches exactly.
    /// <see langword="null"/> means "cannot be expressed", and the caller then checks out
    /// everything rather than guessing.
    /// </summary>
    private static IReadOnlyList<string>? WantedPatterns(
        string treeOutput, string prefix, Func<string, bool> pathFilter)
    {
        var wanted = new List<string>();
        foreach (var raw in treeOutput.Split('\0'))
        {
            var path = raw.Trim('\n', '\r');
            if (path.Length == 0)
                continue;
            if (prefix.Length > 0 && !path.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var subRel = prefix.Length == 0 ? path : path[prefix.Length..];
            if (!pathFilter(subRel))
                continue;
            // A path carrying a gitignore metacharacter would be read as a PATTERN and could
            // select files the caller did not ask for. Refuse the whole narrowing rather than
            // build one wrong pattern.
            if (HasPatternMetacharacter(path))
                return null;
            wanted.Add("/" + path);
            if (wanted.Count > MaxNarrowPaths)
                return null;
        }
        return wanted;
    }

    /// <summary>How many paths the tree carries — the denominator the narrowing is reported against,
    /// so a log line that says "2 of 5" cannot be read without knowing what was passed over.</summary>
    private static int CountPaths(string treeOutput)
        => treeOutput.Split('\0').Count(p => p.Trim('\n', '\r').Length > 0);

    /// <summary>Whether git honoured <c>--filter</c> or told us the server ignored it.</summary>
    private static bool FilterWasIgnored(GitCommandResult result)
        => result.StdErr.Contains("filtering not recognized", StringComparison.OrdinalIgnoreCase);

    /// <summary>Beyond this many matches the patterns stop being worth an argv: a selection this
    /// wide is close to the whole tree anyway, so the whole checkout is both simpler and safe
    /// against the platform's argument-length limit.</summary>
    private const int MaxNarrowPaths = 2000;

    /// <summary>The gitignore-pattern syntax a literal path must not contain to be usable as one.
    /// An immutable, never-written lookup — the one shape a <c>static readonly</c> may take
    /// (<c>Doc/Architecture/NoStaticState</c>).</summary>
    private static readonly System.Buffers.SearchValues<char> PatternMetacharacters =
        System.Buffers.SearchValues.Create("*?[]\\!#");

    private static bool HasPatternMetacharacter(string path)
        => path.AsSpan().IndexOfAny(PatternMetacharacters) >= 0;

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
    /// <remarks>A single REST lookup that follows GitHub's rename redirect — the git wire protocol
    /// cannot answer "what is this repository called now", so this one stays on Octokit.</remarks>
    public IObservable<RepoIdentity?> GetCanonicalRepository(string repositoryUrl, string accessToken)
        => octokit.GetCanonicalRepository(repositoryUrl, accessToken);

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
    public IObservable<GitHubIssue> SetIssueState(
        string repositoryUrl, int number, GitHubIssueState state, string accessToken)
        => octokit.SetIssueState(repositoryUrl, number, state, accessToken);

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
            return (IReadOnlyList<RepoFile>)files.ToImmutableList();
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

    /// <summary>
    /// Normalizes the mirror subdirectory to a <c>segment/…/</c> prefix and REJECTS any shape
    /// that could escape the clone: rooted paths, <c>.</c>/<c>..</c> segments, backslashes. The
    /// prefix names the deletion target of the mirror — defense here must not depend on
    /// upstream request validation.
    /// </summary>
    private static string NormalizePrefix(string? subdirectory)
    {
        var s = subdirectory?.Trim().Trim('/');
        if (string.IsNullOrEmpty(s))
            return "";
        if (s.Contains('\\') || Path.IsPathRooted(s)
            || s.Split('/').Any(seg => seg.Length == 0 || seg is "." or ".."))
            throw new ArgumentException($"Invalid repo subdirectory '{subdirectory}'.");
        return s + "/";
    }
}
