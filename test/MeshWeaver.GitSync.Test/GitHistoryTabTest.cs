using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// GUI test: the "Git history" settings tab shows on a Space, and selecting it renders its content
/// pane — <see cref="GitHistoryTab.BuildContent"/> → BuildBrowser. With no repository connected the
/// browser renders its "connect a repository" guidance; this asserts the tab WIRING (registration +
/// gate + content composition), not git data (that is covered by <see cref="GitHistoryServiceTest"/>).
/// </summary>
public class GitHistoryTabTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    // The base only registers the GitHub-Sync tab; register the Git-history tab too (the portal does
    // this in MemexConfiguration) so it appears on the Settings page under test.
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).ConfigureDefaultNodeHub(c => c.AddGitHistoryTab());

    [Fact(Timeout = 60000)]
    public async Task GitHistoryTab_ShownOnSpace()
    {
        var space = "GhHist" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "History Space");

        var json = await RenderSettings(new Address(space), j => j.GetRawText().Contains("Git history"));
        Assert.Contains("Git history", json);
    }

    [Fact(Timeout = 60000)]
    public async Task GitHistoryTab_Content_Renders()
    {
        var space = "GhHistC" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "History Content Space");

        // Select the Git-history tab so its content pane renders. The H2 title + intro are emitted by
        // BuildContent up front (independent of repo/checkout state), so asserting the intro proves the
        // content pane composed and ran.
        var json = await RenderSettings(
            new Address(space),
            new LayoutAreaReference("Settings") { Id = GitHistoryTab.TabId },
            j => j.GetRawText().Contains("Browse the commit history"));

        Assert.Contains("Git history", json);               // the tab title (H2)
        Assert.Contains("Browse the commit history", json); // BuildContent's intro pane rendered
    }

    private Task<string> RenderSettings(Address hostAddress, Func<JsonElement, bool> until)
        => RenderSettings(hostAddress, new LayoutAreaReference("Settings"), until);

    private async Task<string> RenderSettings(Address hostAddress, LayoutAreaReference reference, Func<JsonElement, bool> until)
    {
        var client = GetClient();
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(hostAddress, reference);
        var change = await stream.Should().Within(30.Seconds()).Match(ci => until(ci.Value));
        return change.Value.GetRawText();
    }
}
