using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.GitSync;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// GitHub <b>issue</b> integration over the in-memory fake: syncing repo issues into
/// <c>{space}/_Issue/{number}</c> nodes, opening a new issue (which materializes its node),
/// and commenting (which posts on GitHub then refreshes the node's comment count + list).
/// </summary>
public class IssueOperationsTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 120000)]
    public async Task SyncIssues_MaterializesRepoIssuesAsNodes()
    {
        await Connect();
        var space = "GhIs" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Issue Space");
        var repo = "https://github.com/test/issue-space";
        await Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();

        var n1 = Fake.SeedIssue(repo, "First issue", "body one");
        var n2 = Fake.SeedIssue(repo, "Second issue", "body two", GitHubIssueState.Closed);

        var count = await Issues.SyncIssues(space, UserId).Timeout(60.Seconds()).ToTask();
        Assert.Equal(2, count);

        var issue1 = await WaitForIssue(IssueService.IssuePath(space, n1), i => i.Title == "First issue");
        Assert.Equal(GitHubIssueState.Open, issue1.State);
        Assert.Equal(n1, issue1.Number);

        var issue2 = await WaitForIssue(IssueService.IssuePath(space, n2), i => i.State == GitHubIssueState.Closed);
        Assert.Equal("Second issue", issue2.Title);

        // The Space lists both synced issue nodes.
        var nodes = await Issues.ListIssueNodes(space).Timeout(30.Seconds()).ToTask();
        Assert.Equal(2, nodes.Count);
    }

    [Fact(Timeout = 120000)]
    public async Task SyncIssues_OpenFilter_ExcludesClosed()
    {
        await Connect();
        var space = "GhIf" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Issue Filter Space");
        var repo = "https://github.com/test/issue-filter";
        await Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();

        Fake.SeedIssue(repo, "Open one");
        Fake.SeedIssue(repo, "Closed one", state: GitHubIssueState.Closed);

        var count = await Issues.SyncIssues(space, UserId, GitHubIssueState.Open).Timeout(60.Seconds()).ToTask();
        Assert.Equal(1, count);
    }

    [Fact(Timeout = 120000)]
    public async Task CreateIssue_OpensOnGitHub_AndMaterializesNode()
    {
        await Connect();
        var space = "GhIc" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Issue Create Space");
        var repo = "https://github.com/test/issue-create";
        await Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();

        var node = await Issues.CreateIssue(space, "New bug", "It broke", new[] { "bug" }, UserId)
            .Timeout(60.Seconds()).ToTask();

        var issue = await WaitForIssue(node.Path, i => i.Title == "New bug");
        Assert.Equal(GitHubIssueState.Open, issue.State);
        Assert.Contains("bug", issue.Labels);
        Assert.False(string.IsNullOrEmpty(issue.Url));
    }

    [Fact(Timeout = 120000)]
    public async Task CommentIssue_PostsOnGitHub_ThenRefreshesNode()
    {
        await Connect();
        var space = "GhIm" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Issue Comment Space");
        var repo = "https://github.com/test/issue-comment";
        await Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();

        var number = Fake.SeedIssue(repo, "Needs discussion");
        await Issues.SyncIssue(space, number, UserId).Timeout(60.Seconds()).ToTask();
        await WaitForIssue(IssueService.IssuePath(space, number), i => i.CommentsCount == 0);

        await Issues.CommentIssue(space, number, "Here is my thought", UserId).Timeout(60.Seconds()).ToTask();

        var updated = await WaitForIssue(IssueService.IssuePath(space, number), i => i.CommentsCount == 1);
        Assert.Contains(updated.Comments, c => c.Body == "Here is my thought");
    }
}
