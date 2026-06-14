using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// GitHub operations over the in-memory fake: create branch, the AI-draft → edit → submit
/// open-PR flow (which writes ONLY the immutable handle — number + url — onto the PullRequest
/// node), live PR status asked from GitHub (never replicated), and update-to-latest.
/// </summary>
public class PullRequestOperationsTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact(Timeout = 120000)]
    public void CreateBranch_FromMain_AddsBranch()
    {
        Connect();
        var space = "GhBr" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(space, "Branch Space");
        CreateMarkdown($"{space}/Page", "Page", "# page");
        var repo = "https://github.com/test/branch-space";
        Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).Wait();
        // A push creates the repo + the main branch so CreateBranch has a base ref.
        Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).Wait();

        var result = PullRequests.CreateBranch(space, "feature/x", "main", UserId)
            .Timeout(60.Seconds()).Wait();

        Assert.Equal("feature/x", result.Branch);
        Assert.False(string.IsNullOrEmpty(result.CommitSha));
        Assert.True(Fake.BranchExists(repo, "feature/x"));
    }

    [Fact(Timeout = 120000)]
    public void OpenPullRequest_AiDraft_Edit_Submit_WritesOnlyImmutableHandleOntoNode()
    {
        Connect();
        var space = "GhPr" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(space, "PR Space");
        CreateMarkdown($"{space}/Page", "Page", "# page");
        var repo = "https://github.com/test/pr-space";
        Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).Wait();
        Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).Wait();

        // Step 1+2: AI drafts the title/body and a draft PR node is created (local state only).
        var draftNode = PullRequests.CreateDraft(space, headBranch: "main", baseBranch: "main")
            .Timeout(60.Seconds()).Wait();
        var prPath = draftNode.Path;

        // The AI-draft step ran with the change context (Space name flowed through to the agent).
        Assert.Contains(DraftStub.Calls, c => c.SpaceName == "PR Space" && c.HeadBranch == "main");

        // A fresh draft has no GitHub handle yet — and we never store a status field.
        var drafted = WaitForPr(prPath, pr => !string.IsNullOrEmpty(pr.Title));
        Assert.False(string.IsNullOrEmpty(drafted.Body));
        Assert.Null(drafted.Number);
        Assert.Null(drafted.Url);
        // Status is NOT replicated — it's asked live; a not-yet-opened PR reports Draft.
        var draftStatus = PullRequests.AskStatus(space, prPath, UserId).Timeout(30.Seconds()).Wait();
        Assert.Equal(PullRequestStatus.Draft, draftStatus.Status);

        // Step 3: the user edits the bound node's Title (the GUI way — a per-field JSON patch via
        // GetMeshNodeStream(path).Update, exactly what MeshNodeContentEditorView writes).
        const string editedTitle = "Edited PR title before submit";
        Mesh.GetMeshNodeStream(prPath)
            .Update(n => n with { Content = PatchField(n.Content, "title", JsonValue.Create(editedTitle)) })
            .Timeout(30.Seconds()).Wait();
        WaitForPr(prPath, pr => pr.Title == editedTitle);

        // Step 4: submit → opens the PR on GitHub and writes ONLY the immutable handle (number+url).
        var info = PullRequests.Submit(space, prPath, UserId).Timeout(60.Seconds()).Wait();
        Assert.True(info.Number > 0);
        Assert.Contains("/pull/", info.Url);
        Assert.Equal(PullRequestStatus.Open, info.Status);

        var opened = WaitForPr(prPath, pr => pr.Number != null);
        Assert.Equal(info.Number, opened.Number);
        Assert.Equal(info.Url, opened.Url);
        Assert.Equal(editedTitle, opened.Title); // the user's edit was used, not the AI default
    }

    [Fact(Timeout = 120000)]
    public void AskStatus_ReadsLiveFromGitHub_NeverReplicatesOntoNode()
    {
        Connect();
        var space = "GhSt" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(space, "Status Space");
        CreateMarkdown($"{space}/Page", "Page", "# page");
        var repo = "https://github.com/test/status-space";
        Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).Wait();
        Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).Wait();

        var draftNode = PullRequests.CreateDraft(space, "main", "main").Timeout(60.Seconds()).Wait();
        var prPath = draftNode.Path;
        var info = PullRequests.Submit(space, prPath, UserId).Timeout(60.Seconds()).Wait();
        WaitForPr(prPath, pr => pr.Number != null);

        // Open → AskStatus reads Open live from GitHub.
        Assert.Equal(PullRequestStatus.Open,
            PullRequests.AskStatus(space, prPath, UserId).Timeout(60.Seconds()).Wait().Status);

        // Simulate GitHub merging the PR. AskStatus reflects the new state immediately — because it
        // delegates to GitHub rather than reading a replicated field.
        Fake.SetPullRequestStatus(repo, info.Number, PullRequestStatus.Merged);
        Assert.Equal(PullRequestStatus.Merged,
            PullRequests.AskStatus(space, prPath, UserId).Timeout(60.Seconds()).Wait().Status);

        // The node itself holds NO status field — only the immutable handle. (GitHubPullRequest has
        // no Status property; this is a compile-time guarantee, asserted here as the node round-trips
        // with the handle but the live status came from AskStatus, not the node.)
        var node = WaitForPr(prPath, pr => pr.Number == info.Number);
        Assert.Equal(info.Number, node.Number);
    }

    [Fact(Timeout = 120000)]
    public void UpdateToLatest_ReimportsAtBranchHead()
    {
        Connect();
        var space = "GhUp" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(space, "Update Space");
        CreateMarkdown($"{space}/Welcome", "Welcome", "# Welcome\n\nv1.");
        var repo = "https://github.com/test/update-space";
        Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).Wait();
        Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).Wait();

        // Add a node, sync again (the repo HEAD now has Extra), then locally "delete" it by
        // re-importing main HEAD which still has both — assert update-to-latest mirrors HEAD.
        CreateMarkdown($"{space}/Extra", "Extra", "# Extra\n\nlater.");
        Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).Wait();

        var result = PullRequests.UpdateToLatest(space, UserId).Timeout(90.Seconds()).Wait();
        Assert.True(result.Count >= 1);
        Assert.NotNull(WaitForNode($"{space}/Extra"));
        Assert.NotNull(WaitForNode($"{space}/Welcome"));
    }

    [Fact(Timeout = 120000)]
    public void AskBranchState_ReadsLiveHeadFromGitHub()
    {
        Connect();
        var space = "GhBs" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(space, "Branch State Space");
        CreateMarkdown($"{space}/Page", "Page", "# v1");
        var repo = "https://github.com/test/branch-state";
        Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).Wait();

        var c1 = Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).Wait();
        // Right after a sync, the Space is up to date with the branch HEAD it just wrote.
        var st1 = Sync.AskBranchState(space, UserId).Timeout(60.Seconds()).Wait();
        Assert.Equal("main", st1.Branch);
        Assert.Equal(c1.CommitSha, st1.HeadCommitSha);
        Assert.True(st1.UpToDate);

        // Someone else pushes to the branch (a new commit on GitHub) WITHOUT recording it as the
        // Space's last sync — asking again reports the Space is now behind, because the HEAD comes
        // LIVE from GitHub, not from a stored copy.
        var external = Fake.Push(new GitHubPushRequest
        {
            RepositoryUrl = repo,
            Branch = "main",
            Files = System.Collections.Immutable.ImmutableList.Create(new RepoFile("External.md", "# external")),
            CommitMessage = "external",
            AuthorName = "other",
            AuthorEmail = "other@example.com",
            AccessToken = "t",
        }).Timeout(30.Seconds()).Wait();

        var st2 = Sync.AskBranchState(space, UserId).Timeout(60.Seconds()).Wait();
        Assert.Equal(external.CommitSha, st2.HeadCommitSha);   // live HEAD, not the stored last-sync
        Assert.False(st2.UpToDate);                            // Space's last sync != branch HEAD
    }

    private GitHubPullRequest WaitForPr(string prPath, Func<GitHubPullRequest, bool> predicate) =>
        PullRequests.WatchPullRequest(prPath)
            .Where(pr => pr is not null && predicate(pr))
            .Select(pr => pr!)
            .FirstAsync()
            .Timeout(30.Seconds())
            .Wait();

    private JsonElement PatchField(object? content, string key, JsonNode value)
    {
        var opts = Mesh.JsonSerializerOptions;
        var obj = (content is JsonElement je
            ? JsonNode.Parse(je.GetRawText())
            : JsonSerializer.SerializeToNode(content, opts)) as JsonObject ?? new JsonObject();
        obj[key] = JsonNode.Parse(value.ToJsonString());
        return JsonSerializer.SerializeToElement<object>(obj, opts);
    }
}
