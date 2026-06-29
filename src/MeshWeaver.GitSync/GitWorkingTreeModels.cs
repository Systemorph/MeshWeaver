namespace MeshWeaver.GitSync;

/// <summary>A checked-out per-user working tree on disk.</summary>
/// <param name="UserId">The owning user (partition segment under the workspace root).</param>
/// <param name="RepoSlug">The repository directory name (the repo's <c>name</c>, e.g. <c>MeshWeaver</c>).</param>
/// <param name="Path">Absolute path of the working tree on disk.</param>
/// <param name="Branch">The currently checked-out branch.</param>
public sealed record WorkingTree(string UserId, string RepoSlug, string Path, string Branch);

/// <summary>One changed path in a working tree, as reported by <c>git status --porcelain</c>.</summary>
/// <param name="Path">Repo-relative path.</param>
/// <param name="Status">The two-char porcelain code (e.g. <c>M</c>, <c>A</c>, <c>D</c>, <c>??</c>).</param>
public sealed record GitFileChange(string Path, string Status);

/// <summary>Working-tree status: current branch + whether it is clean + the pending changes.</summary>
public sealed record WorkingTreeStatus(string Branch, bool IsClean, IReadOnlyList<GitFileChange> Changes);

/// <summary>Raw result of one <c>git</c> invocation. <see cref="Ok"/> is exit code 0.</summary>
public sealed record GitCommandResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>True when the command succeeded (exit code 0).</summary>
    public bool Ok => ExitCode == 0;

    /// <summary>StdErr if present, else StdOut — the human-facing message for a failed command.</summary>
    public string Message => string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr;
}
