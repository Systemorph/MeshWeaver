using System.Collections.Immutable;
using MeshWeaver.GitSync;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Pins the <b>incremental-fetch default interface members</b> on <see cref="IGitHubRepoClient"/>,
/// added so pollers can (1) check a repo's head SHA with ONE call via
/// <see cref="IGitHubRepoClient.GetHeadSha"/> and re-fetch only when it moved, and (2) download
/// only the files a manifest scan needs via the path-filtered
/// <see cref="IGitHubRepoClient.Fetch(string, string, string?, string, Func{string, bool})"/>
/// overload — instead of pulling every blob in the repo on every poll (which exhausted the
/// GitHub App rate limit).
///
/// <para>Both members are default interface implementations (full fetch + post-filter, resp. the
/// full fetch's <see cref="RepoSnapshot.CommitSha"/>), so every existing implementor stays correct
/// and source-compatible without changes. <see cref="FakeGitHubRepoClient"/> deliberately does NOT
/// override either member — calling them through the interface exercises the DIMs themselves.</para>
/// </summary>
public class IncrementalFetchDefaultsTest
{
    private const string Repo = "https://github.com/acme/plugin-registry";

    private static IObservable<GitHubPushResult> Seed(FakeGitHubRepoClient fake) =>
        fake.Push(new GitHubPushRequest
        {
            RepositoryUrl = Repo,
            CommitMessage = "seed",
            AuthorName = "tester",
            AuthorEmail = "tester@example.com",
            AccessToken = "",
            Files = ImmutableList.Create(
                new RepoFile("Slides/index.json", """{ "id": "Slides" }"""),
                new RepoFile("Slides/Slide/Source/Slide.cs", "// live-compiled source"),
                new RepoFile("Edu/index.json", """{ "id": "Edu" }"""),
                new RepoFile("README.md", "# readme")),
        });

    [Fact]
    public async Task FilteredFetchDefault_ReturnsOnlyMatchingFiles_AtTheSameCommit()
    {
        var fake = new FakeGitHubRepoClient();
        var pushed = await Seed(fake).Should().Emit();
        IGitHubRepoClient client = fake; // interface call => the DIM, not an override

        var snapshot = await client
            .Fetch(Repo, pushed.CommitSha, null, "",
                path => path.EndsWith("/index.json", StringComparison.Ordinal))
            .Should().Emit();

        snapshot.CommitSha.Should().Be(pushed.CommitSha);
        snapshot.Files.Select(f => f.Path).Should().Equal("Slides/index.json", "Edu/index.json");
    }

    [Fact]
    public async Task FilteredFetchDefault_FilterSeesSubdirectoryRelativePaths()
    {
        var fake = new FakeGitHubRepoClient();
        var pushed = await Seed(fake).Should().Emit();
        IGitHubRepoClient client = fake;

        // Within subdirectory "Slides" the manifest's relative path is exactly "index.json".
        var snapshot = await client
            .Fetch(Repo, pushed.CommitSha, "Slides", "", path => path == "index.json")
            .Should().Emit();

        snapshot.CommitSha.Should().Be(pushed.CommitSha);
        snapshot.Files.Select(f => f.Path).Should().Equal("index.json");
    }

    [Fact]
    public async Task GetHeadShaDefault_ReturnsTheFetchedCommitSha()
    {
        var fake = new FakeGitHubRepoClient();
        var pushed = await Seed(fake).Should().Emit();
        IGitHubRepoClient client = fake;

        var sha = await client.GetHeadSha(Repo, "main", "").Should().Emit();

        sha.Should().Be(pushed.CommitSha);
    }
}
