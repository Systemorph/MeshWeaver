using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Memex.Portal.Shared.Settings;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The What's New tab, end to end through a node hub, is a TEMPLATE (Doc/GUI/DataBinding →
/// "Templates first, data later"): its feed is a <see cref="MarkdownControl"/> whose text is a
/// POINTER into <c>/data/whatsNewList</c>, and the synced listing's rendering reaches that slot —
/// including an entry created while the tab is open.
///
/// <para>What it was: a deferred view, <c>GetQuery(…).Select(nodes =&gt; Controls.Markdown(Render(nodes)))</c>,
/// that rendered nothing but a loading line until the listing answered, then rebuilt the control on
/// every emission. The "renders before its data" half is pinned for the helper itself in
/// <c>MeshWeaver.Layout.Test/FedControlsAreTemplatesTest</c> with a feed held silent; this test pins
/// that the tab is wired to it.</para>
///
/// <para>Negative control, run before this file was committed: against the pre-conversion tab the
/// first wait fails — no frame carries a pointer-bound markdown.</para>
/// </summary>
public class WhatsNewTabIsATemplateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).ConfigureDefaultNodeHub(config => config.AddPlatformSettingsTabAreas());

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [Fact]
    public async Task TheFeedIsABoundMarkdown_AndFollowsTheListing()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var ns = $"WhatsNewTpl{Guid.NewGuid():N}"[..20];
        await meshService.CreateNode(new MeshNode("Host", ns)
        {
            Name = "Host", NodeType = "Markdown", State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address($"{ns}/Host"), new LayoutAreaReference(PlatformSettingsTabAreas.WhatsNewArea));

        var feed = await stream.Select(change => change.Value)
            .Select(frame => FindBoundMarkdown(GetClient().JsonSerializerOptions, frame))
            .Should().Within(TestTimeouts.Convergence).Match(m => m is not null,
                "the tab declares its feed as a markdown control bound by pointer");
        ((JsonPointerReference)feed!.Markdown).Pointer.Should().Contain("whatsNewList");

        var text = stream.DataBind<string>(new JsonPointerReference(LayoutAreaReference.GetDataPointer("whatsNewList")));

        await meshService.CreateNode(new MeshNode("2026-10-01-a-template-entry", ns)
        {
            NodeType = WhatsNewSettingsTab.EntryNodeType,
            Name = "An entry created while the tab is open",
            State = MeshNodeState.Active,
        }).Should().Within(TestTimeouts.Convergence).Emit();

        await text.Should().Within(TestTimeouts.Convergence).Match(
            t => t.Contains("An entry created while the tab is open", StringComparison.Ordinal),
            "the listing's rendering reaches the SAME bound slot, live");
    }

    private static MarkdownControl? FindBoundMarkdown(JsonSerializerOptions options, JsonElement frame)
    {
        if (!frame.TryGetProperty("areas", out var areas))
            return null;
        foreach (var area in areas.EnumerateObject())
            if (area.Value.TryGetProperty("$type", out var t) && t.GetString() == nameof(MarkdownControl)
                && area.Value.Deserialize<UiControl>(options) is MarkdownControl
                    { Markdown: JsonPointerReference } markdown)
                return markdown;
        return null;
    }
}
