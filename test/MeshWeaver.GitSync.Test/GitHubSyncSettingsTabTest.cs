using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// GUI test: the "GitHub Sync" settings tab renders on the Settings page of any node WITHIN a
/// Space (the Space root and its descendants — always referring to the containing Space), and is
/// hidden outside a Space (a top-level non-Space partition). The provider resolves the containing
/// Space from the current node's path (first segment) and gates on it being a Space.
/// </summary>
public class GitHubSyncSettingsTabTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [Fact(Timeout = 60000)]
    public async Task GitHubSyncTab_ShownOnSpace()
    {
        var space = "GhGui" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Gui Space");

        // The Settings area resolves its tabs reactively; wait for an emission that
        // includes the GitHub Sync tab label.
        var json = await RenderSettings(new Address(space), j => j.GetRawText().Contains("GitHub Sync"));
        Assert.Contains("GitHub Sync", json);
    }

    [Fact(Timeout = 60000)]
    public async Task GitHubSyncTab_HiddenOnNonSpace()
    {
        // TestData is a top-level Markdown partition (seeded by the test base) — NOT a Space, and
        // not inside one. Its containing partition root (itself) is not a Space, so the tab is
        // hidden. Wait until the Settings page has settled (a default tab present), then assert absent.
        var json = await RenderSettings(new Address(TestPartition), j => j.GetRawText().Contains("Metadata"));
        Assert.DoesNotContain("GitHub Sync", json);
    }

    [Fact(Timeout = 60000)]
    public async Task GitHubSyncTab_ShownOnChildNodeWithinSpace()
    {
        // A node INSIDE a Space (not the Space root) must also surface the tab — it acts on the
        // containing Space. Create a Space, then a child node under it, and render the CHILD's
        // Settings page: the GitHub Sync tab resolves the containing Space and shows there too.
        var space = "GhChild" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Child Space");
        var childPath = $"{space}/Docs/Readme";
        await CreateMarkdown(childPath, "Readme", "# hi");

        var json = await RenderSettings(new Address(childPath), j => j.GetRawText().Contains("GitHub Sync"));
        Assert.Contains("GitHub Sync", json);
    }

    [Fact(Timeout = 60000)]
    public async Task GitHubSyncTab_Content_RendersPullRequestSection()
    {
        var space = "GhPrGui" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "PR Gui Space");

        // Select the GitHub Sync tab so its content pane (BuildContent) renders — wait until the
        // PR section's draft button appears. The one-click sync actions (Sync now / Update to latest
        // / Check branch) MOVED to the "GitHub" node-menu dropdown (settings = setup only); the tab
        // now points the user there and keeps the input-driven PR + re-import flows.
        // The note is data-bound (renders async relative to the static sections) — wait for its
        // plain-text tail so we assert against the snapshot that actually carries it.
        var json = await RenderSettings(
            new Address(space),
            new LayoutAreaReference("Settings") { Id = GitHubSyncSettingsTab.TabId },
            j => j.GetRawText().Contains("Draft pull request with AI")
                 && j.GetRawText().Contains("shown once a repository is configured"));

        Assert.Contains("Draft pull request with AI", json);
        Assert.Contains("Pull request", json);                          // the section heading
        Assert.Contains("shown once a repository is configured", json);  // note → the moved actions
        Assert.DoesNotContain("Sync now (commit)", json);               // one-click action no longer in the tab
    }

    [Fact(Timeout = 60000)]
    public async Task OpeningGitHubSyncTab_AutoCreatesConfigNodeForUnconfiguredSpace()
    {
        var space = "GhAuto" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Auto Space");
        var cfgPath = GitHubSyncService.ConfigPath(space);

        // The Space was NEVER configured — no _GitSync node yet.
        Assert.True(await IsAbsent(cfgPath));

        // Open the GitHub Sync tab. The Repository editor is gated on EnsureConfigNode, so the
        // editor's "Repository URL" field only renders once {space}/_GitSync has been auto-created.
        var json = await RenderSettings(
            new Address(space),
            new LayoutAreaReference("Settings") { Id = GitHubSyncSettingsTab.TabId },
            j => j.GetRawText().Contains("Repository URL"));
        Assert.Contains("Repository URL", json);

        // Entering the GUI auto-created the config node (with defaults) — robust for un-configured Spaces.
        var node = await WaitForNode(cfgPath);
        Assert.Equal(GitHubSyncService.ConfigNodeType, node.NodeType);
        var cfg = await WaitForConfig(space, _ => true);
        Assert.Equal("main", cfg.Branch);
    }

    private Task<string> RenderSettings(Address hostAddress, Func<JsonElement, bool> until)
        => RenderSettings(hostAddress, new LayoutAreaReference("Settings"), until);

    private async Task<string> RenderSettings(Address hostAddress, LayoutAreaReference reference, Func<JsonElement, bool> until)
    {
        var client = GetClient();
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(hostAddress, reference);
        // The remote stream emits ChangeItem<JsonElement>; match on the rendered area's value.
        var change = await stream.Should().Within(30.Seconds()).Match(ci => until(ci.Value));
        return change.Value.GetRawText();
    }
}
