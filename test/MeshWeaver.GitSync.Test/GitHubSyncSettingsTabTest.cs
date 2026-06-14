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
/// GUI test: the "GitHub Sync" settings tab renders on a Space's Settings page and is
/// hidden on non-Space nodes (the provider self-filters on <c>NodeType == "Space"</c>).
/// </summary>
public class GitHubSyncSettingsTabTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [Fact(Timeout = 60000)]
    public void GitHubSyncTab_ShownOnSpace()
    {
        var space = "GhGui" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(space, "Gui Space");

        // The Settings area resolves its tabs reactively; wait for an emission that
        // includes the GitHub Sync tab label.
        var json = RenderSettings(new Address(space), j => j.GetRawText().Contains("GitHub Sync"));
        Assert.Contains("GitHub Sync", json);
    }

    [Fact(Timeout = 60000)]
    public void GitHubSyncTab_HiddenOnNonSpace()
    {
        // TestData is a Markdown node (seeded by the test base) — not a Space. Wait until
        // the Settings page has settled (a default tab present), then assert our tab is absent.
        var json = RenderSettings(new Address(TestPartition), j => j.GetRawText().Contains("Metadata"));
        Assert.DoesNotContain("GitHub Sync", json);
    }

    [Fact(Timeout = 60000)]
    public void GitHubSyncTab_Content_RendersPullRequestSection()
    {
        var space = "GhPrGui" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(space, "PR Gui Space");

        // Select the GitHub Sync tab so its content pane (BuildContent) renders — wait until the
        // PR section's draft button text appears. This proves the new "Pull request" section is
        // wired into the layout area, not just the tab label.
        var json = RenderSettings(
            new Address(space),
            new LayoutAreaReference("Settings") { Id = GitHubSyncSettingsTab.TabId },
            j => j.GetRawText().Contains("Draft pull request with AI"));

        Assert.Contains("Draft pull request with AI", json);
        Assert.Contains("Pull request", json);          // the section heading
        Assert.Contains("Sync now (commit)", json);     // the commit action renders in the tab
        Assert.Contains("Update to latest (checkout)", json);  // the checkout action renders too
    }

    [Fact(Timeout = 60000)]
    public void OpeningGitHubSyncTab_AutoCreatesConfigNodeForUnconfiguredSpace()
    {
        var space = "GhAuto" + Guid.NewGuid().ToString("N")[..8];
        CreateSpace(space, "Auto Space");
        var cfgPath = GitHubSyncService.ConfigPath(space);

        // The Space was NEVER configured — no _GitSync node yet.
        Assert.True(IsAbsent(cfgPath));

        // Open the GitHub Sync tab. The Repository editor is gated on EnsureConfigNode, so the
        // editor's "Repository URL" field only renders once {space}/_GitSync has been auto-created.
        var json = RenderSettings(
            new Address(space),
            new LayoutAreaReference("Settings") { Id = GitHubSyncSettingsTab.TabId },
            j => j.GetRawText().Contains("Repository URL"));
        Assert.Contains("Repository URL", json);

        // Entering the GUI auto-created the config node (with defaults) — robust for un-configured Spaces.
        var node = WaitForNode(cfgPath);
        Assert.Equal(GitHubSyncService.ConfigNodeType, node.NodeType);
        var cfg = WaitForConfig(space, _ => true);
        Assert.Equal("main", cfg.Branch);
    }

    private string RenderSettings(Address hostAddress, Func<JsonElement, bool> until)
        => RenderSettings(hostAddress, new LayoutAreaReference("Settings"), until);

    private string RenderSettings(Address hostAddress, LayoutAreaReference reference, Func<JsonElement, bool> until)
    {
        var client = GetClient();
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(hostAddress, reference);
        // The remote stream emits ChangeItem<JsonElement>; match on the rendered area's value.
        var change = stream.Should().Within(30.Seconds()).Match(ci => until(ci.Value));
        return change.Value.GetRawText();
    }
}
