using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Issue #6814 — <c>$Menu:AppSettings</c> listed EVERY <see cref="UiContribution.AppSettingsContext"/>
/// contribution on EVERY node, with an href rebased onto the node being viewed: the menu projection
/// ignored both <see cref="UiContribution.Host"/> and <see cref="UiContribution.Address"/>, while the
/// settings-tab projection of the very same contribution honoured both.
///
/// <para>Pinned end to end through the layout client, on the RENDERED menu. Each node carries a
/// CONTROL entry hosted on itself, so "the probe is absent" is a verdict on a rendered menu and
/// never a frame caught before the contributions were projected.</para>
/// </summary>
public class AppSettingsMenuHonoursHostTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string HostApp = "AsmHost";
    private const string Other = "AsmOther";
    private const string AppPartition = "AsmApp";
    private const string Workspace = AppPartition + "/Workspace";
    private const string ProbeArea = "ProbeSettings";
    private const string ControlArea = "ControlSettings";

    private static MeshNode Contribution(string id, string host, string area, string? address) =>
        new(id, AppPartition + "/AppSettings")
        {
            Name = id,
            NodeType = UiContributionNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new UiContribution
            {
                Context = UiContribution.AppSettingsContext,
                Host = host,
                Address = address,
                Area = area,
                Label = id,
            },
        };

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // The harness viewer's grant (base.ConfigureMesh) — the entries demand Read, and the
        // per-node control proves the viewer holds it, so an absence is the host gate's verdict.
        => base.ConfigureMesh(builder)
            .AddMeshNodes(
                new MeshNode(HostApp) { Name = "Host app" },
                new MeshNode(Other) { Name = "Other node" },
                new MeshNode(AppPartition) { Name = "App partition" },
                new MeshNode("Workspace", AppPartition) { Name = "App workspace" },
                // The shape of Signature/AppSettings/Providers: hosted on one app, embedding the
                // module's own workspace hub.
                Contribution("Probe", HostApp, ProbeArea, Workspace),
                // Controls — one per node, no address: each must appear on its own host only.
                Contribution("HostControl", HostApp, ControlArea, null),
                Contribution("OtherControl", Other, ControlArea, null));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition));

    /// <summary>The rendered <c>$Menu:AppSettings</c> of <paramref name="node"/>, once its control is in it.</summary>
    private async Task<IReadOnlyList<NodeMenuItemDefinition>> AppSettingsMenu(string node)
    {
        // The menu renderer enumerates DATA-declared contexts from the catalog's synchronous
        // snapshot at render time (UiContributionCatalog.Current — documented as possibly cold on
        // first access). Wait for the catalog's own answer first, so the AppSettings context exists
        // when the node's area renders — what a running portal, whose catalog is long warm, sees.
        await Mesh.ServiceProvider.GetRequiredService<UiContributionCatalog>().Contributions
            .Where(set => set.Count(c => c.Content.Context == UiContribution.AppSettingsContext) >= 3)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await();

        return await GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(node), new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea))
            .GetControlStream(MenuControl.GetMenuArea(UiContribution.AppSettingsContext))
            .Where(x => x is MenuControl)
            .Select(x => (IReadOnlyList<NodeMenuItemDefinition>)((MenuControl)x!).Items)
            .Where(items => items.Any(i => i.Area == ControlArea))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await();
    }

    /// <summary>
    /// Off its host the entry is NOT offered — the other node's own control proves the menu
    /// rendered and the contributions were projected.
    /// </summary>
    [HubFact]
    public async Task AnAppSettingsEntry_IsNotOfferedOnANodeThatIsNotItsHost()
    {
        var items = await AppSettingsMenu(Other);
        Output.WriteLine("AsmOther $Menu:AppSettings: " + string.Join(", ", items.Select(i => $"{i.Area} → {i.Href}")));

        Assert.DoesNotContain(items, i => i.Area == ProbeArea);
        var control = Assert.Single(items, i => i.Area == ControlArea);
        Assert.Equal(MeshNodeLayoutAreas.BuildUrl(Other, ControlArea), control.Href);
    }

    /// <summary>
    /// On its host the entry IS offered, and it links to its area on the declared ADDRESS — the hub
    /// the matching settings tab embeds — never to the host rebased (<c>/AsmHost/ProbeSettings</c>).
    /// </summary>
    [HubFact]
    public async Task OnItsHost_TheEntryIsOffered_AndLinksToItsDeclaredAddress()
    {
        var items = await AppSettingsMenu(HostApp);
        Output.WriteLine("AsmHost $Menu:AppSettings: " + string.Join(", ", items.Select(i => $"{i.Area} → {i.Href}")));

        var probe = Assert.Single(items, i => i.Area == ProbeArea);
        Assert.Equal(MeshNodeLayoutAreas.BuildUrl(Workspace, ProbeArea), probe.Href);
        Assert.DoesNotContain(items, i => i.Href == MeshNodeLayoutAreas.BuildUrl(Other, ControlArea));
    }
}
