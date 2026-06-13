using System.Reactive.Linq;
using MeshWeaver.GitSync;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// In-memory <see cref="IGitHubRepoClient"/> for offline, deterministic tests. Models the
/// real client's contract: a single-commit mirror within the configured subdirectory, a
/// per-commit history (so re-import at an earlier commit returns that earlier state), and
/// auto-create of a missing repo. No statics, no network, no randomness — commit shas are an
/// incrementing counter so the loop tests are reproducible.
/// </summary>
public sealed class FakeGitHubRepoClient : IGitHubRepoClient
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<RepoFile>> _head = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<RepoFile>> _byCommit = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _headSha = new(StringComparer.Ordinal);
    private readonly HashSet<string> _exists = new(StringComparer.Ordinal);
    private int _counter;

    /// <summary>The current full tree of a repo (for test assertions).</summary>
    public IReadOnlyList<RepoFile> Tree(string repositoryUrl)
    {
        lock (_lock) return _head.TryGetValue(Key(repositoryUrl), out var t) ? t.ToList() : new List<RepoFile>();
    }

    public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            var key = Key(request.RepositoryUrl);
            var created = _exists.Add(key) && !_head.ContainsKey(key);
            var prefix = Norm(request.Subdirectory);
            var existing = _head.TryGetValue(key, out var cur) ? cur : new List<RepoFile>();

            var newUnder = request.Files.Select(f => new RepoFile(prefix + f.Path, f.Content)).ToList();
            var exportPaths = newUnder.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var outside = existing
                .Where(f => prefix.Length != 0 && !f.Path.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            var deleted = existing.Count(f =>
                (prefix.Length == 0 || f.Path.StartsWith(prefix, StringComparison.Ordinal))
                && !exportPaths.Contains(f.Path));

            var newTree = outside.Concat(newUnder).ToList();
            _head[key] = newTree;
            var sha = NextSha();
            _byCommit[sha] = newTree;
            _headSha[key] = sha;
            return Observable.Return(new GitHubPushResult(sha, request.RepositoryUrl, request.Files.Count, deleted, created));
        }
    });

    public IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string commitish, string? subdirectory, string accessToken) => Observable.Defer(() =>
    {
        lock (_lock)
        {
            var key = Key(repositoryUrl);
            IReadOnlyList<RepoFile> tree;
            string sha;
            if (_byCommit.TryGetValue(commitish, out var byCommit))
            {
                tree = byCommit;
                sha = commitish;
            }
            else if (_head.TryGetValue(key, out var head))
            {
                tree = head;
                sha = _headSha.TryGetValue(key, out var h) ? h : "head";
            }
            else
            {
                return Observable.Throw<RepoSnapshot>(
                    new InvalidOperationException($"Fake repo '{repositoryUrl}' has no commit for '{commitish}'."));
            }

            var prefix = Norm(subdirectory);
            var files = tree
                .Where(f => prefix.Length == 0 || f.Path.StartsWith(prefix, StringComparison.Ordinal))
                .Select(f => new RepoFile(prefix.Length == 0 ? f.Path : f.Path[prefix.Length..], f.Content))
                .ToList();
            return Observable.Return(new RepoSnapshot(sha, files));
        }
    });

    private string NextSha() => (++_counter).ToString("x").PadLeft(40, '0');

    private static string Key(string url)
    {
        var (owner, repo) = OctokitGitHubRepoClient.ParseRepoUrl(url);
        return $"{owner}/{repo}".ToLowerInvariant();
    }

    private static string Norm(string? s)
    {
        var t = s?.Trim().Trim('/');
        return string.IsNullOrEmpty(t) ? "" : t + "/";
    }
}
