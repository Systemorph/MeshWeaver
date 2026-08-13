using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.GitSync;

/// <summary>One module's source, materialised on disk at an exact commit.</summary>
/// <param name="RepoSlug">Sanitised repo identity, e.g. <c>Systemorph_MeshWeaver</c>.</param>
/// <param name="Sha">The commit the worktree is pinned to — the version key.</param>
/// <param name="ModulePath">Repo-relative directory that was checked out (sparse).</param>
/// <param name="AbsolutePath">Absolute path of the module inside its worktree.</param>
public sealed record ModuleCheckout(string RepoSlug, string Sha, string ModulePath, string AbsolutePath);

/// <summary>Tuning for <see cref="GitModuleReplica"/>.</summary>
public sealed class GitModuleReplicaOptions
{
    /// <summary>
    /// Where the permanent clones and the per-module worktrees live.
    ///
    /// <para>🚨 A DEPLOYMENT MUST POINT THIS AT A PERSISTENT VOLUME. The default is a temp path so a
    /// test or a dev run needs no configuration, but on a pod temp is ephemeral: the replica would be
    /// re-cloned on every restart, which is exactly the cost it exists to remove. The portal already
    /// mounts one (16 GB at <c>/data</c>, holding <c>assembly-cache</c> and <c>nuget-cache</c>), and
    /// this belongs beside them — the source key and the assembly it produces should share a
    /// lifetime.</para>
    /// </summary>
    public string RootDirectory { get; set; } =
        Path.Combine(Path.GetTempPath(), "meshweaver-source-replica");

    /// <summary>
    /// How many commits' worth of worktrees to keep per repo before <see cref="GitModuleReplica.Prune"/>
    /// removes the rest. Two, so a bake can still read the previous commit while the new one
    /// materialises; more only costs disk.
    /// </summary>
    public int KeepCommitsPerRepo { get; set; } = 2;
}

/// <summary>
/// A shared, commit-pinned, on-disk replica of synced repos — one permanent clone per repo, plus a
/// SPARSE worktree per (commit, module). It answers "give me this module's sources at this commit"
/// from disk, so work keyed on a commit can be skipped entirely when the commit has not moved.
///
/// <para><b>Why this exists.</b> Compilation currently reads every source through the mesh, and each
/// touched path costs synchronisation state: a recompile of a trivial NodeType mints ~22 <c>sync/</c>
/// sub-hubs and retains ~9 MB, of which only about 9% is the type itself — the rest is the compile's
/// own bookkeeping nodes and the cache's mirrors of them (issue #1324). Reading sources from a
/// commit-pinned checkout removes those reads from the mesh entirely, and the commit gives the
/// invalidation key for free: unchanged commit ⇒ nothing to do.</para>
///
/// <para><b>Sparse on purpose.</b> A plain <c>git worktree add</c> materialises the WHOLE tree, so one
/// worktree per module would multiply the repo across every module. The clone's object store is
/// ~758 MB for MeshWeaver while the node content is single-digit MB, so worktrees are created
/// <c>--no-checkout</c>, narrowed with cone-mode sparse-checkout, and only then checked out.</para>
///
/// <para><b>Reactive to the IO boundary.</b> Every git invocation is a blocking process leaf bridged
/// through <see cref="IIoPool"/> by <see cref="GitCli"/>; file-system probes run on the
/// <see cref="IoPoolNames.FileSystem"/> pool. All methods return COLD observables — the work runs on
/// Subscribe. No <c>async</c>/<c>await</c>, no <c>Task</c> on the public surface.</para>
///
/// <para><b>Not a second source of truth.</b> The replica is authoritative only for content that comes
/// FROM git. A NodeType whose source is edited in the portal is keyed by node version, not by commit,
/// and must not be read from here — otherwise the two stores drift and the people iterating in the
/// editor are served stale sources. Callers decide; this class only materialises what it is asked
/// for.</para>
/// </summary>
public sealed class GitModuleReplica(
    GitCli git,
    IoPoolRegistry ioPools,
    IOptions<GitModuleReplicaOptions> options,
    ILogger<GitModuleReplica>? logger = null)
{
    private string Root => options.Value.RootDirectory;
    private IIoPool FileSystem => ioPools.Get(IoPoolNames.FileSystem);

    /// <summary>The permanent clone for a repo — checked out on its default branch.</summary>
    public string RepoPath(string repoSlug) =>
        Path.Combine(Root, "repos", Sanitize(repoSlug));

    /// <summary>The sparse worktree holding one module at one commit.</summary>
    public string WorktreePath(string repoSlug, string sha, string modulePath) =>
        Path.Combine(Root, "worktrees", Sanitize(repoSlug), Sanitize(sha), Sanitize(modulePath));

    /// <summary>
    /// Ensures the permanent clone exists and contains <paramref name="sha"/>, fetching only when the
    /// commit is missing — a repo that already has it costs one <c>cat-file</c> and no network.
    /// </summary>
    public IObservable<string> EnsureRepo(string repoSlug, string remoteUrl, string sha, string? token = null) =>
        Observable.Defer(() =>
        {
            var dest = RepoPath(repoSlug);
            var env = GitCredentials.AuthEnv(token);
            var auth = GitCredentials.AuthArgs(token);

            return FileSystem.InvokeBlocking(_ => Directory.Exists(Path.Combine(dest, ".git")))
                .SelectMany(exists =>
                {
                    if (!exists)
                    {
                        var parent = Path.Combine(Root, "repos");
                        Directory.CreateDirectory(parent);
                        logger?.LogInformation(
                            "GitModuleReplica: cloning {Repo} into the replica (first use)", repoSlug);
                        return Expect(git.Run(
                            parent, [.. auth, "clone", remoteUrl, Sanitize(repoSlug)], env));
                    }

                    // Present already? Then no network at all — this is the fast path that makes an
                    // unchanged commit free.
                    return git.Run(dest, ["cat-file", "-e", $"{sha}^{{commit}}"])
                        .SelectMany(probe => probe.ExitCode == 0
                            ? Observable.Return(probe)
                            : Expect(git.Run(dest, [.. auth, "fetch", "origin", "--tags"], env)));
                })
                .Select(_ => dest);
        });

    /// <summary>
    /// Materialises <paramref name="modulePath"/> at <paramref name="sha"/> and returns its checkout.
    /// IDEMPOTENT and cheap on a hit: an existing worktree already at that commit is returned after a
    /// single <c>rev-parse</c>, with no fetch, no checkout and no disk writes.
    /// </summary>
    public IObservable<ModuleCheckout> Materialize(
        string repoSlug, string remoteUrl, string sha, string modulePath, string? token = null) =>
        Observable.Defer(() =>
        {
            var wt = WorktreePath(repoSlug, sha, modulePath);
            var result = new ModuleCheckout(
                Sanitize(repoSlug), sha, modulePath, Path.Combine(wt, modulePath));

            return AlreadyAt(wt, sha).SelectMany(hit => hit
                ? Observable.Return(result)
                : EnsureRepo(repoSlug, remoteUrl, sha, token)
                    .SelectMany(repo => Create(repo, wt, sha, modulePath))
                    .Select(_ => result));
        });

    /// <summary>
    /// Removes worktrees for commits outside <paramref name="keepShas"/> and prunes git's
    /// administrative records. Returns how many were removed.
    ///
    /// <para>🚨 Called by whoever creates worktrees, not by a timer. A retention rule whose trigger
    /// nobody owns is how the in-memory version of this problem got here: the stream cache's idle
    /// release is correct code behind a predicate that never becomes true. Keep the owner explicit.</para>
    /// </summary>
    public IObservable<int> Prune(string repoSlug, IReadOnlyCollection<string> keepShas) =>
        Observable.Defer(() =>
        {
            var slugDir = Path.Combine(Root, "worktrees", Sanitize(repoSlug));
            var keep = keepShas.Select(Sanitize).ToImmutableHashSet(StringComparer.Ordinal);

            return FileSystem.InvokeBlocking(_ => Directory.Exists(slugDir)
                    ? Directory.GetDirectories(slugDir)
                    : [])
                .SelectMany(dirs =>
                {
                    var stale = dirs
                        .Where(d => !keep.Contains(Path.GetFileName(d)))
                        .SelectMany(d => Directory.Exists(d) ? Directory.GetDirectories(d) : [])
                        .ToImmutableArray();
                    if (stale.IsEmpty)
                        return Observable.Return(0);

                    var repo = RepoPath(repoSlug);
                    return stale
                        .Select(path => git.Run(repo, ["worktree", "remove", "--force", path]))
                        .Concat()                       // one at a time: git's admin file is not concurrent
                        .ToList()
                        .SelectMany(_ => git.Run(repo, ["worktree", "prune"]))
                        .Select(_ =>
                        {
                            logger?.LogInformation(
                                "GitModuleReplica: pruned {Count} superseded worktree(s) for {Repo}, "
                                + "keeping {Keep} commit(s)", stale.Length, repoSlug, keep.Count);
                            return stale.Length;
                        });
                });
        });

    /// <summary>True when a worktree already sits at exactly this commit.</summary>
    private IObservable<bool> AlreadyAt(string worktree, string sha) =>
        FileSystem.InvokeBlocking(_ => Directory.Exists(worktree))
            .SelectMany(exists => exists
                ? git.Run(worktree, ["rev-parse", "HEAD"])
                    .Select(r => r.ExitCode == 0
                                 && string.Equals(r.StdOut.Trim(), sha, StringComparison.OrdinalIgnoreCase))
                : Observable.Return(false));

    /// <summary>
    /// <c>--no-checkout</c> → cone sparse-checkout → <c>checkout</c>. The order is the point: adding a
    /// worktree normally materialises the entire tree, so narrowing it BEFORE the checkout is what
    /// keeps a module's worktree the size of that module.
    /// </summary>
    private IObservable<GitCommandResult> Create(string repo, string worktree, string sha, string modulePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
        return Expect(git.Run(repo, ["worktree", "add", "--detach", "--no-checkout", worktree, sha]))
            .SelectMany(_ => Expect(git.Run(worktree, ["sparse-checkout", "init", "--cone"])))
            .SelectMany(_ => Expect(git.Run(worktree, ["sparse-checkout", "set", modulePath])))
            .SelectMany(_ => Expect(git.Run(worktree, ["checkout"])))
            .Do(_ => logger?.LogInformation(
                "GitModuleReplica: materialised {Module} at {Sha} (sparse)", modulePath, Short(sha)));
    }

    private static IObservable<GitCommandResult> Expect(IObservable<GitCommandResult> op) =>
        op.SelectMany(r => r.ExitCode == 0
            ? Observable.Return(r)
            : Observable.Throw<GitCommandResult>(new GitWorkingTreeException(
                $"git failed ({r.ExitCode}): {(string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr)}")));

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    /// <summary>One path segment, with no separators or traversal — every input here reaches the file system.</summary>
    private static string Sanitize(string value)
    {
        var cleaned = value.Replace('/', '_').Replace('\\', '_').Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            cleaned = cleaned.Replace(c, '_');
        if (cleaned is "" or "." or "..")
            throw new GitWorkingTreeException($"'{value}' is not a usable path segment.");
        return cleaned;
    }
}
