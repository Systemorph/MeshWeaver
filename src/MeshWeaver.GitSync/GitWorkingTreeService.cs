using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.GitSync;

/// <summary>Thrown when a git working-tree operation fails (non-zero git exit).</summary>
public sealed class GitWorkingTreeException(string message) : Exception(message);

/// <summary>
/// Per-user on-disk git working trees: clone a GitHub repo onto the workspace volume, read/write
/// files, commit, and push — the working-tree counterpart to <see cref="GitHubSyncService"/>'s
/// content sync. Both the co-hosted AI harness (Claude Code / Copilot) and the in-portal editor
/// operate on the SAME tree at <c>{Root}/{userId}/{repoSlug}</c> (<see cref="GitWorkingTreeOptions"/>).
///
/// <para>🚨 Reactive end-to-end — no <c>async</c>/<c>await</c>/<c>Task</c> on the public surface.
/// Every git invocation is a blocking <see cref="System.Diagnostics.Process"/> leaf bridged through
/// <see cref="IIoPool"/> by <see cref="GitCli"/>; file reads/writes run on the
/// <see cref="IoPoolNames.FileSystem"/> pool. All methods return <b>cold</b> observables — the work
/// runs on <c>Subscribe</c>.</para>
///
/// <para>Auth: the user's GitHub token (from <see cref="GitHubCredentialService"/>) is injected into
/// git through an inline credential helper that reads it from the <c>GW_TOKEN</c> environment
/// variable — the secret never appears in argv (visible to <c>ps</c>) or persists in
/// <c>.git/config</c>. Commit identity is the user's GitHub login + no-reply email.</para>
/// </summary>
public sealed class GitWorkingTreeService(
    GitCli git,
    GitHubCredentialService credentials,
    IoPoolRegistry ioPools,
    IOptions<GitWorkingTreeOptions> options)
{
    private string Root => options.Value.Root;
    private IIoPool FileSystem => ioPools.Get(IoPoolNames.FileSystem);

    /// <summary>Absolute path of a user's working tree for a repo.</summary>
    public string PathFor(string userId, string repoSlug) =>
        Path.Combine(Root, Sanitize(userId), Sanitize(repoSlug));

    // ── High-level (credential-resolving) — used by the GUI editor + the AI harness ──────────

    /// <summary>
    /// Clones <paramref name="repoFullName"/> (<c>owner/repo</c>) for the user — or fetches + fast-forwards
    /// an existing checkout — using the user's stored GitHub credential. Errors if none is connected.
    /// </summary>
    public IObservable<WorkingTree> Checkout(string userId, string repoFullName, string? branch = null) =>
        credentials.Get(userId).SelectMany(cred =>
        {
            if (cred?.AccessToken is not { Length: > 0 } token)
                return Observable.Throw<WorkingTree>(new GitWorkingTreeException(
                    "No GitHub credential — connect GitHub first."));
            var url = $"https://x-access-token@github.com/{repoFullName}.git";
            return CloneOrUpdate(userId, RepoSlug(repoFullName), url, branch, token, cred.GitHubLogin);
        });

    /// <summary>Stages all changes, commits as the user, and pushes — the editor's "Commit" action.</summary>
    public IObservable<GitCommandResult> CommitAndPush(string userId, string repoSlug, string message, string? branch = null) =>
        credentials.Get(userId).SelectMany(cred =>
        {
            var login = cred?.GitHubLogin;
            var name = string.IsNullOrEmpty(login) ? "MeshWeaver" : login;
            var email = string.IsNullOrEmpty(login) ? "noreply@meshweaver.cloud" : $"{login}@users.noreply.github.com";
            return Commit(userId, repoSlug, message, name, email)
                .SelectMany(commit => !commit.Ok
                    ? Observable.Return(commit) // e.g. "nothing to commit" — surface, don't push or throw
                    : Push(userId, repoSlug, branch, cred?.AccessToken).Select(_ => commit));
        });

    // ── Low-level (explicit) — directly unit-testable against a local file:// remote ──────────

    /// <summary>
    /// Clones <paramref name="remoteUrl"/> into <c>{Root}/{userId}/{repoSlug}</c>, or — if already
    /// cloned — fetches origin, checks out <paramref name="branch"/> (when given), and fast-forwards.
    /// </summary>
    public IObservable<WorkingTree> CloneOrUpdate(
        string userId, string repoSlug, string remoteUrl, string? branch, string? token, string? authorLogin = null) =>
        Observable.Defer(() =>
        {
            var userDir = Path.Combine(Root, Sanitize(userId));
            var dest = Path.Combine(userDir, Sanitize(repoSlug));
            var env = AuthEnv(token);
            var auth = AuthArgs(token);

            IObservable<GitCommandResult> op;
            if (Directory.Exists(Path.Combine(dest, ".git")))
            {
                op = Expect(git.Run(dest, [.. auth, "fetch", "origin"], env));
                if (branch is not null)
                    op = op.SelectMany(_ => Expect(git.Run(dest, ["checkout", branch])));
                op = op.SelectMany(_ => Expect(git.Run(dest, [.. auth, "pull", "--ff-only"], env)));
            }
            else
            {
                Directory.CreateDirectory(userDir);
                List<string> clone = [.. auth, "clone"];
                if (branch is not null) { clone.Add("--branch"); clone.Add(branch); }
                clone.Add(remoteUrl);
                clone.Add(Sanitize(repoSlug));
                op = Expect(git.Run(userDir, clone, env));
            }

            return op.SelectMany(_ => CurrentBranch(dest))
                .Select(b => new WorkingTree(userId, repoSlug, dest, b));
        });

    /// <summary><c>git status</c> for the working tree: current branch, clean flag, and pending changes.</summary>
    public IObservable<WorkingTreeStatus> Status(string userId, string repoSlug)
    {
        var dest = PathFor(userId, repoSlug);
        // --untracked-files=all lists each new file individually instead of collapsing a brand-new
        // directory to "?? dir/" — the editor's change list needs per-file granularity.
        return Expect(git.Run(dest, ["status", "--porcelain=v1", "--branch", "--untracked-files=all"]))
            .Select(ParseStatus);
    }

    /// <summary>Stages everything and commits (no push). Returns the raw result — a clean tree yields a non-Ok "nothing to commit".</summary>
    public IObservable<GitCommandResult> Commit(string userId, string repoSlug, string message, string authorName, string authorEmail)
    {
        var dest = PathFor(userId, repoSlug);
        return Expect(git.Run(dest, ["add", "-A"]))
            .SelectMany(_ => git.Run(dest,
            [
                "-c", $"user.name={authorName}",
                "-c", $"user.email={authorEmail}",
                "-c", "commit.gpgsign=false",
                "commit", "-m", message,
            ]));
    }

    /// <summary>Pushes the current branch (or <paramref name="branch"/>) to origin.</summary>
    public IObservable<Unit> Push(string userId, string repoSlug, string? branch, string? token)
    {
        var dest = PathFor(userId, repoSlug);
        return Expect(git.Run(dest, [.. AuthArgs(token), "push", "origin", branch ?? "HEAD"], AuthEnv(token)))
            .Select(_ => Unit.Default);
    }

    /// <summary>Reads a repo-relative file from the working tree (UTF-8).</summary>
    public IObservable<string> ReadFile(string userId, string repoSlug, string relativePath) =>
        FileSystem.InvokeBlocking(_ => File.ReadAllText(ResolveInTree(userId, repoSlug, relativePath)));

    /// <summary>Writes a repo-relative file in the working tree (UTF-8), creating directories as needed.</summary>
    public IObservable<Unit> WriteFile(string userId, string repoSlug, string relativePath, string content) =>
        FileSystem.InvokeBlocking<Unit>(_ =>
        {
            var full = ResolveInTree(userId, repoSlug, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return Unit.Default;
        });

    /// <summary>Lists tracked files (repo-relative, forward-slashed) — the source of the editor's file tree.</summary>
    public IObservable<IReadOnlyList<string>> ListFiles(string userId, string repoSlug)
    {
        var dest = PathFor(userId, repoSlug);
        return Expect(git.Run(dest, ["ls-files"]))
            .Select(r => (IReadOnlyList<string>)r.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    // ── git browser (read-only history) ──────────────────────────────────────────────────────

    /// <summary>
    /// The latest <paramref name="maxCount"/> commits on the working tree's current branch (newest
    /// first) — the commit log the git browser renders. Fields are unit-separator delimited so a
    /// subject containing any other character parses cleanly.
    /// </summary>
    public IObservable<IReadOnlyList<GitCommit>> Log(string userId, string repoSlug, int maxCount = 100)
    {
        // Clamp: 0/negative makes `git log -n` show nothing or error; an unbounded value would let a
        // caller pull the whole history into the portal. 1000 is a generous ceiling for a browser.
        var n = Math.Clamp(maxCount, 1, 1000);
        var dest = PathFor(userId, repoSlug);
        return Expect(git.Run(dest,
        [
            "log", $"-n{n}", "--date=format:%Y-%m-%d %H:%M",
            // %H %h %ad %an %s, separated by the 0x1f unit separator (%x1f).
            "--pretty=format:%H%x1f%h%x1f%ad%x1f%an%x1f%s",
        ])).Select(ParseLog);
    }

    /// <summary>
    /// The files changed by <paramref name="commitHash"/> against its parent (status + repo-relative
    /// path). <c>--root</c> makes the initial commit list its files as added rather than empty;
    /// <c>--no-renames</c> keeps every line a simple <c>STATUS\tpath</c> (a rename shows as D + A).
    /// </summary>
    public IObservable<IReadOnlyList<GitFileChange>> CommitChanges(string userId, string repoSlug, string commitHash)
    {
        // A blank hash, or one starting with '-', would be parsed by git as a flag (argument injection)
        // rather than a revision — reject it before it reaches the CLI.
        if (string.IsNullOrWhiteSpace(commitHash) || commitHash.StartsWith('-'))
            return Observable.Throw<IReadOnlyList<GitFileChange>>(
                new GitWorkingTreeException($"Invalid commit '{commitHash}'."));
        var dest = PathFor(userId, repoSlug);
        return Expect(git.Run(dest,
            ["diff-tree", "--no-commit-id", "--name-status", "--no-renames", "-r", "--root", commitHash]))
            .Select(ParseNameStatus);
    }

    /// <summary>
    /// The content of <paramref name="relativePath"/> at git revision <paramref name="rev"/> (e.g.
    /// <c>"HEAD"</c>, a commit hash, or <c>"{hash}^"</c> for a parent) — the two sides a diff needs.
    /// <para>Returns <c>""</c> ONLY when the path genuinely did not exist at that revision (an added or
    /// deleted file — its empty side is a legitimate diff input). Any OTHER git failure (invalid
    /// revision, repo not checked out, …) is propagated as a <see cref="GitWorkingTreeException"/>
    /// rather than masked as empty, so a real fault is never silently swallowed.</para>
    /// </summary>
    public IObservable<string> ShowFile(string userId, string repoSlug, string rev, string relativePath)
    {
        // Neither side may start with '-' (git would parse it as a flag — argument injection).
        if (rev.StartsWith('-') || relativePath.StartsWith('-'))
            return Observable.Throw<string>(
                new GitWorkingTreeException($"Invalid revision '{rev}' or path '{relativePath}'."));
        var dest = PathFor(userId, repoSlug);
        return git.Run(dest, ["show", $"{rev}:{relativePath}"])
            .SelectMany(r => r.Ok
                ? Observable.Return(r.StdOut)
                : IsPathAbsentAtRev(r.StdErr)
                    ? Observable.Return("")
                    : Observable.Throw<string>(new GitWorkingTreeException(
                        $"git show {rev}:{relativePath} failed (exit {r.ExitCode}): {r.Message}")));
    }

    /// <summary>True when <c>git show</c>'s stderr signals the path simply wasn't in that revision
    /// (vs. a real failure like a bad rev) — the messages git emits for an added/deleted file.</summary>
    private static bool IsPathAbsentAtRev(string stderr) =>
        stderr.Contains("does not exist in", StringComparison.Ordinal)
        || stderr.Contains("exists on disk, but not in", StringComparison.Ordinal);

    // ── internals ─────────────────────────────────────────────────────────────────────────

    private IObservable<string> CurrentBranch(string dest) =>
        Expect(git.Run(dest, ["rev-parse", "--abbrev-ref", "HEAD"])).Select(r => r.StdOut.Trim());

    /// <summary>Passes Ok results through; converts a non-zero git exit into a typed error.</summary>
    private IObservable<GitCommandResult> Expect(IObservable<GitCommandResult> op) =>
        op.SelectMany(r => r.Ok
            ? Observable.Return(r)
            : Observable.Throw<GitCommandResult>(new GitWorkingTreeException($"git failed (exit {r.ExitCode}): {r.Message}")));

    private static WorkingTreeStatus ParseStatus(GitCommandResult r)
    {
        var branch = "";
        var changes = new List<GitFileChange>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("## "))
            {
                // "## main...origin/main [ahead 1]" → branch is up to the first '.' or space.
                var rest = line[3..];
                var cut = rest.IndexOfAny(['.', ' ']);
                branch = cut < 0 ? rest : rest[..cut];
                continue;
            }
            if (line.Length < 3) continue;
            changes.Add(new GitFileChange(line[3..].Trim(), line[..2].Trim()));
        }
        return new WorkingTreeStatus(branch, changes.Count == 0, changes);
    }

    /// <summary>The 0x1f unit separator that delimits <c>git log</c> fields (can't occur in commit text).</summary>
    private const char LogFieldSeparator = '\x1f';

    private static IReadOnlyList<GitCommit> ParseLog(GitCommandResult r)
    {
        var commits = new List<GitCommit>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // count: 5 → the subject keeps everything after the 4th separator even if it held one.
            var f = line.Split(LogFieldSeparator, 5);
            if (f.Length < 5) continue;
            commits.Add(new GitCommit(f[0], f[1], f[2], f[3], f[4]));
        }
        return commits;
    }

    private static IReadOnlyList<GitFileChange> ParseNameStatus(GitCommandResult r)
    {
        var changes = new List<GitFileChange>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            changes.Add(new GitFileChange(line[(tab + 1)..].Trim(), line[..tab].Trim()));
        }
        return changes;
    }

    /// <summary>The credential-helper config that reads the token from <c>$GW_TOKEN</c> (token never in argv).</summary>
    private static IReadOnlyList<string> AuthArgs(string? token) => string.IsNullOrEmpty(token)
        ? []
        : [
            // Clear any inherited helper (system credential store), then install ours.
            "-c", "credential.helper=",
            "-c", "credential.helper=!f() { test \"$1\" = get && printf 'username=x-access-token\\npassword=%s\\n' \"$GW_TOKEN\"; }; f",
          ];

    private static IReadOnlyDictionary<string, string>? AuthEnv(string? token) =>
        string.IsNullOrEmpty(token) ? null : new Dictionary<string, string> { ["GW_TOKEN"] = token };

    /// <summary>Resolves a repo-relative path and guarantees it stays inside the user's working tree.</summary>
    private string ResolveInTree(string userId, string repoSlug, string relativePath)
    {
        var dest = Path.GetFullPath(PathFor(userId, repoSlug));
        var full = Path.GetFullPath(Path.Combine(dest, relativePath));
        if (!full.StartsWith(dest + Path.DirectorySeparatorChar, StringComparison.Ordinal) && full != dest)
            throw new GitWorkingTreeException($"Path '{relativePath}' escapes the working tree.");
        return full;
    }

    /// <summary>The repo directory name from an <c>owner/repo</c> full name (e.g. <c>Systemorph/MeshWeaver</c> → <c>MeshWeaver</c>).</summary>
    private static string RepoSlug(string repoFullName)
    {
        var name = repoFullName.TrimEnd('/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>One path segment only — no separators or traversal, so a user/repo id can't escape the root.</summary>
    private static string Sanitize(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".."
            || segment.Contains('/') || segment.Contains('\\') || segment.Contains(".."))
            throw new GitWorkingTreeException($"Invalid path segment '{segment}'.");
        return segment;
    }
}
