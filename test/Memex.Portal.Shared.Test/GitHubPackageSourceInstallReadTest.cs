using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 The package.json source's INSTALL read through the narrow fetch is scoped by the
/// SUBDIRECTORY argument, and only by it (MeshWeaver#5826): it passes the package's folder as the
/// subdirectory and a keep-everything predicate, and — unlike <see cref="NodeRepoPackageSource"/>,
/// whose snapshot paths are repo-relative — it cannot post-filter, because the paths it gets back
/// are already relative to that folder.
///
/// <para>So the answer is right exactly when the folder reaches the fetch. That is the filtered
/// fetch's contract (<see cref="IGitHubRepoClient"/>: "like Fetch … but downloads ONLY the blobs
/// whose subdirectory-relative path satisfies pathFilter"; the Octokit and git-protocol clients and
/// the interface's default all scope by the prefix first), and this pins the CALL: a refactor that
/// dropped the folder, or handed the listing's manifest-only predicate to the install, would
/// install the whole repository or an empty package with no other test noticing.</para>
/// </summary>
public class GitHubPackageSourceInstallReadTest
{
    private const string RepoUrl = "https://github.com/acme/catalog";

    private static readonly IReadOnlyList<RepoFile> Repo =
    [
        new("welcome-note/package.json", """{"id":"welcome-note"}"""),
        new("welcome-note/content/Note.md", "# Welcome"),
        new("welcome-note/content/logo.png", "", [1, 2, 3]),
        new("other/package.json", """{"id":"other"}"""),
        new("other/content/Secret.md", "# Not this package"),
    ];

    /// <summary>What a fetch was asked for.</summary>
    private sealed record Call(string? Subdirectory, bool Narrow);

    /// <summary>A repository that honours the fetch contract: confine to the subdirectory, return
    /// subdirectory-relative paths, keep what the predicate keeps.</summary>
    private sealed class ContractRepo
    {
        public ImmutableList<Call> Calls { get; private set; } = ImmutableList<Call>.Empty;

        public IObservable<RepoSnapshot> Plain(string url, string gitRef, string? subdirectory, string token)
            => Read(subdirectory, static _ => true, narrow: false);

        public IObservable<RepoSnapshot> Narrow(
            string url, string gitRef, string? subdirectory, string token, Func<string, bool> keep)
            => Read(subdirectory, keep, narrow: true);

        private IObservable<RepoSnapshot> Read(string? subdirectory, Func<string, bool> keep, bool narrow)
            => Observable.Defer(() =>
            {
                Calls = Calls.Add(new Call(subdirectory, narrow));
                var prefix = string.IsNullOrEmpty(subdirectory) ? "" : subdirectory.TrimEnd('/') + "/";
                var files = Repo
                    .Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(f => f with { Path = f.Path[prefix.Length..] })
                    .Where(f => keep(f.Path))
                    .ToList();
                return Observable.Return(new RepoSnapshot("commit-1", files));
            });
    }

    private static Task<IReadOnlyList<PackageFile>> Files(GitHubPackageSource source) =>
        source.FetchPackageFiles(new PackageManifest { Id = "welcome-note" }, "main")
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheNarrowInstallRead_AsksForThePackagesFolder_AndReturnsExactlyThatFolder()
    {
        var repo = new ContractRepo();
        var narrow = new GitHubPackageSource(repo.Plain, RepoUrl) { NarrowFetch = repo.Narrow };

        var files = await Files(narrow);

        repo.Calls.Should().Equal([new Call("welcome-note", Narrow: true)],
            "the install read goes through the narrow fetch, once, and the package's folder is its "
            + "subdirectory — the only thing that scopes this read");
        files.Select(f => f.RelativePath).OrderBy(path => path, StringComparer.Ordinal).Should().Equal(
            ["content/Note.md", "content/logo.png", "package.json"],
            "the WHOLE folder — the keep-everything predicate, never the listing's manifest-only one "
            + "— and nothing of any other package");
        files.Single(f => f.RelativePath == "content/logo.png").Binary.Should().Equal([1, 2, 3],
            "a binary blob travels as bytes (#848)");
    }

    [Fact]
    public async Task TheNarrowRead_AndThePlainRead_GiveTheSameAnswer()
    {
        var narrowRepo = new ContractRepo();
        var plainRepo = new ContractRepo();
        var narrow = await Files(new GitHubPackageSource(narrowRepo.Plain, RepoUrl) { NarrowFetch = narrowRepo.Narrow });
        var plain = await Files(new GitHubPackageSource(plainRepo.Plain, RepoUrl));

        plainRepo.Calls.Should().Equal([new Call("welcome-note", Narrow: false)],
            "the control: without a narrow fetch the plain one is asked for the same folder");
        narrow.Select(f => $"{f.RelativePath}|{f.Content}|{f.Binary?.Length}").Should().Equal(
            plain.Select(f => $"{f.RelativePath}|{f.Content}|{f.Binary?.Length}"),
            "the answer must not depend on which of the two reads served it");
    }
}
