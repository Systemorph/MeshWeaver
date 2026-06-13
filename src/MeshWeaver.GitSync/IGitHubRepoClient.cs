namespace MeshWeaver.GitSync;

/// <summary>
/// The seam over the GitHub repository API used by <see cref="GitHubSyncService"/>.
/// The production implementation (<see cref="OctokitGitHubRepoClient"/>) talks to
/// GitHub via the Octokit Git Data API with every call routed through
/// <see cref="MeshWeaver.Mesh.Threading.IIoPool"/>; tests substitute an in-memory
/// fake so the full export/import loop runs offline and deterministically.
///
/// <para>Both methods return cold <see cref="IObservable{T}"/> — the work runs on
/// Subscribe, never on call. No <c>async</c>/<c>await</c>/<c>Task</c> escapes this
/// boundary: the implementation bridges every Octokit <c>…Async</c> leaf through
/// the I/O pool.</para>
/// </summary>
public interface IGitHubRepoClient
{
    /// <summary>
    /// Mirrors <see cref="GitHubPushRequest.Files"/> into the repository as a single
    /// commit (blobs → tree → commit → update ref), creating the repo private if
    /// missing. Emits the resulting commit SHA.
    /// </summary>
    IObservable<GitHubPushResult> Push(GitHubPushRequest request);

    /// <summary>
    /// Reads every file under <paramref name="subdirectory"/> at the given
    /// <paramref name="commitish"/> (a branch name OR a commit SHA) — commit →
    /// recursive tree → blob per file — and emits them as text, along with the
    /// resolved commit SHA. Used by both the import-into-a-new-Space flow and the
    /// "re-import at a chosen commit" flow.
    /// </summary>
    IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken);
}

/// <summary>A point-in-time snapshot of a repo subtree — the resolved commit SHA + its files.</summary>
public record RepoSnapshot(string CommitSha, IReadOnlyList<RepoFile> Files);
