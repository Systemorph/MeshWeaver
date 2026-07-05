using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.GitSync;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// The richer pull-request surface (all delegated LIVE to GitHub, never persisted): listing every
/// PR, reading one PR's detail (mergeability + check/review roll-ups), commenting, and merging.
/// </summary>
public class PullRequestRicherTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 120000)]
    public async Task ListAll_Detail_Comment_Merge_FlowThroughToGitHub()
    {
        await Connect();
        var space = "GhPl" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "PR List Space");
        var repo = "https://github.com/test/pr-list-space";
        await Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();

        // Seed an open PR on the fake repo (as if opened directly on GitHub).
        var opened = await Fake.OpenPullRequest(new GitHubOpenPullRequestRequest
        {
            RepositoryUrl = repo,
            Title = "Add feature",
            Body = "adds the feature",
            HeadBranch = "feature/x",
            BaseBranch = "main",
            AccessToken = "t",
        }).Timeout(30.Seconds()).ToTask();

        // List (all) surfaces it.
        var all = await PullRequests.ListAll(space, null, UserId).Timeout(60.Seconds()).ToTask();
        Assert.Contains(all, s => s.Number == opened.Number && s.Title == "Add feature" && s.Status == PullRequestStatus.Open);

        // Detail is delegated live.
        var detail = await PullRequests.GetDetail(space, opened.Number, UserId).Timeout(60.Seconds()).ToTask();
        Assert.Equal(opened.Number, detail.Number);
        Assert.True(detail.Mergeable);
        Assert.Equal(GitHubCheckState.None, detail.Checks.Overall);

        // Comment posts on GitHub.
        var comment = await PullRequests.Comment(space, opened.Number, "LGTM", UserId).Timeout(60.Seconds()).ToTask();
        Assert.Equal("LGTM", comment.Body);

        // Merge (squash) closes it as Merged.
        var merge = await PullRequests.Merge(space, opened.Number, GitHubMergeMethod.Squash, null, null, UserId)
            .Timeout(60.Seconds()).ToTask();
        Assert.True(merge.Merged);

        // A closed-filter list now reports it Merged (live from GitHub, never a stored field).
        var closed = await PullRequests.ListAll(space, PullRequestStatus.Closed, UserId).Timeout(60.Seconds()).ToTask();
        Assert.Contains(closed, s => s.Number == opened.Number && s.Status == PullRequestStatus.Merged);
    }
}
