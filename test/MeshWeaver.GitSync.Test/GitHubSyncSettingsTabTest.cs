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

    private string RenderSettings(Address hostAddress, Func<JsonElement, bool> until)
    {
        var client = GetClient();
        var reference = new LayoutAreaReference("Settings");
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(hostAddress, reference);
        // The remote stream emits ChangeItem<JsonElement>; match on the rendered area's value.
        var change = stream.Should().Within(30.Seconds()).Match(ci => until(ci.Value));
        return change.Value.GetRawText();
    }
}
