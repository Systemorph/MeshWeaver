#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MeshWeaver.GitSync;

namespace MeshWeaver.Hosting.Test.GitProviders;

/// <summary>
/// One git repository held in memory — commits (each a full tree of path → bytes plus its parent),
/// branches and a default branch — so the SAME history can be served to <see cref="GitHubSyncService"/>
/// through two providers: as GitHub by <see cref="InMemoryGitHubRepoClient"/> (the
/// <see cref="IGitHubRepoClient"/> seam the GitSync suites fake), and as Azure Repos by
/// <see cref="FakeAzureDevOpsServer"/> (the Azure DevOps REST API, at the HTTP boundary).
///
/// <para>State is one immutable value swapped atomically (<see cref="ImmutableInterlocked"/>), so
/// the concurrent blob reads a fetch issues never see a half-applied commit and no lock is held.</para>
/// </summary>
public sealed class InMemoryGitRemote
{
    /// <summary>A commit: its id, its parent (null for a root commit), its message and full tree.</summary>
    public sealed record Commit(string Sha, string? Parent, string Message, ImmutableSortedDictionary<string, byte[]> Tree);

    private sealed record State(
        ImmutableDictionary<string, Commit> Commits,
        ImmutableDictionary<string, string> Branches,
        string? DefaultBranch,
        int Counter);

    private State state = new(
        ImmutableDictionary<string, Commit>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal),
        null,
        0);

    /// <summary>The branch a repository's first push creates becomes its default branch.</summary>
    public string? DefaultBranch => Volatile.Read(ref state).DefaultBranch;

    /// <summary>Every commit, by id.</summary>
    public IReadOnlyDictionary<string, Commit> Commits => Volatile.Read(ref state).Commits;

    /// <summary>The head of <paramref name="branch"/>, or null when it does not exist.</summary>
    public string? Head(string branch) => Volatile.Read(ref state).Branches.GetValueOrDefault(branch);

    /// <summary>Every branch and its head.</summary>
    public IReadOnlyDictionary<string, string> Branches => Volatile.Read(ref state).Branches;

    /// <summary>The tree at the head of <paramref name="branch"/>; empty when it does not exist.</summary>
    public ImmutableSortedDictionary<string, byte[]> TreeAt(string branch)
        => Head(branch) is { } sha ? Commits[sha].Tree : EmptyTree;

    /// <summary>An empty tree, ordered by ordinal path (git's order, never the culture's).</summary>
    public static ImmutableSortedDictionary<string, byte[]> EmptyTree { get; } =
        ImmutableSortedDictionary<string, byte[]>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>The UTF-8 text of every file at the head of <paramref name="branch"/>.</summary>
    public ImmutableSortedDictionary<string, string> TextTreeAt(string branch)
        => TreeAt(branch).ToImmutableSortedDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value), StringComparer.Ordinal);

    /// <summary>Resolves a full or abbreviated (≥ 7) commit id; null when none matches.</summary>
    public Commit? FindCommit(string id)
    {
        var commits = Volatile.Read(ref state).Commits;
        if (commits.TryGetValue(id, out var exact))
            return exact;
        if (id.Length < 7)
            return null;
        var matches = commits.Values.Where(c => c.Sha.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>The commit ids reachable from <paramref name="sha"/>, itself first.</summary>
    public IReadOnlyList<string> Ancestry(string sha)
    {
        var commits = Volatile.Read(ref state).Commits;
        var chain = new List<string>();
        for (var current = (string?)sha; current is not null && commits.TryGetValue(current, out var c); current = c.Parent)
            chain.Add(c.Sha);
        return chain;
    }

    /// <summary>The newest commit reachable from both, or null.</summary>
    public string? MergeBase(string a, string b)
    {
        var fromB = Ancestry(b).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Ancestry(a).FirstOrDefault(fromB.Contains);
    }

    /// <summary>
    /// Writes a commit onto <paramref name="branch"/> whose tree is <paramref name="edit"/> applied
    /// to the branch's current tree (or <paramref name="baseSha"/>'s, for a branch created by this
    /// commit) — "someone commits to the repository". Refused when the branch's head is not
    /// <paramref name="expectedHead"/>, the same optimistic check a push makes.
    /// </summary>
    public Commit CommitTo(
        string branch,
        Func<ImmutableSortedDictionary<string, byte[]>, ImmutableSortedDictionary<string, byte[]>> edit,
        string message,
        string? expectedHead = null,
        bool checkExpectedHead = false,
        string? baseSha = null)
    {
        Commit? written = null;
        ImmutableInterlocked.Update(ref state, s =>
        {
            var head = s.Branches.GetValueOrDefault(branch);
            if (checkExpectedHead && !string.Equals(head, expectedHead, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"stale head: '{branch}' is at {head ?? "(none)"}, the write expected {expectedHead ?? "(none)"}");
            var parent = head ?? baseSha;
            var tree = parent is not null && s.Commits.TryGetValue(parent, out var p)
                ? p.Tree
                : EmptyTree;
            var counter = s.Counter + 1;
            var newTree = edit(tree);
            var sha = Sha(counter, parent, newTree);
            written = new Commit(sha, parent, message, newTree);
            return s with
            {
                Commits = s.Commits.SetItem(sha, written),
                Branches = s.Branches.SetItem(branch, sha),
                DefaultBranch = s.DefaultBranch ?? branch,
                Counter = counter,
            };
        });
        return written!;
    }

    /// <summary>Points <paramref name="branch"/> at an existing commit (a branch create).</summary>
    public bool CreateBranch(string branch, string sha)
    {
        var created = false;
        ImmutableInterlocked.Update(ref state, s =>
        {
            created = !s.Branches.ContainsKey(branch) && s.Commits.ContainsKey(sha);
            return created ? s with { Branches = s.Branches.SetItem(branch, sha) } : s;
        });
        return created;
    }

    /// <summary>Sets one text file in a tree (a helper for repo-side edits).</summary>
    public static Func<ImmutableSortedDictionary<string, byte[]>, ImmutableSortedDictionary<string, byte[]>> WithFile(
        string path, string text)
        => tree => tree.SetItem(path, Encoding.UTF8.GetBytes(text));

    /// <summary>Removes one file from a tree (a helper for repo-side edits).</summary>
    public static Func<ImmutableSortedDictionary<string, byte[]>, ImmutableSortedDictionary<string, byte[]>> WithoutFile(
        string path)
        => tree => tree.Remove(path);

    /// <summary>The git blob id Azure DevOps reports for a file's bytes.</summary>
    public static string BlobId(byte[] bytes) => AzureReposRepoClient.GitBlobId(bytes);

    private static string Sha(int counter, string? parent, ImmutableSortedDictionary<string, byte[]> tree)
    {
        var text = new StringBuilder().Append(counter).Append('|').Append(parent);
        foreach (var (path, bytes) in tree)
            text.Append('|').Append(path).Append('=').Append(BlobId(bytes));
#pragma warning disable CA5350 // a commit id, as git spells one; it secures nothing
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text.ToString())));
#pragma warning restore CA5350
    }
}
