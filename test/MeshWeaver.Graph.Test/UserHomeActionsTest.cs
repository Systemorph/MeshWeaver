using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The header actions row on a user's HOME (the User landing page): Edit and the More dropdown beside
/// the page, as every standard node page has them. The User page declines the framework header, so
/// before this the NodeActions sub-area did not exist there and the owner's menu was reachable only
/// from the portal's top bar (Plugins#2787).
///
/// <para>Rendered through the layout client as a person sees it. The assertion is the stable CSS class
/// of the More trigger, never its translated word. An ordinary child page is the control: the same
/// probe finds More there, so a miss on the home is a verdict and not a probe that cannot see it.</para>
/// </summary>
public class UserHomeActionsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Person = "home-person";
    private const string Space = "HomeSpace";
    private const string Page = Space + "/Page";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Person)
                {
                    NodeType = UserNodeType.NodeType,
                    Name = "Home Person",
                    State = MeshNodeState.Active,
                    Content = new User { FullName = "Home Person", Email = $"{Person}@meshweaver.io" },
                },
                AssignmentNodeFactory.UserRole(Person, "Admin", Person),
                new MeshNode(Space) { Name = "Home Space", NodeType = "Space" },
                new MeshNode("Page", Space) { Name = "A Page" },
                AssignmentNodeFactory.UserRole(Person, "Admin", Space));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition));

    private void ActAsPerson() => TestUsers.DevLogin(Mesh,
        new AccessContext { ObjectId = Person, Name = "Home Person", Email = $"{Person}@meshweaver.io" });

    private IObservable<string> Render(string address, string area)
    {
        ActAsPerson();
        return GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(address), new LayoutAreaReference(area))
            .Select(change => change.Value.GetRawText().ToLowerInvariant());
    }

    [Fact(Timeout = 60000)]
    public async Task TheOwnersHome_DrawsMore_WithItsEntries()
    {
        var page = await Render(Person, UserActivityLayoutAreas.ActivityArea)
            .Should().Within(Budget)
            .Match(json => json.Contains(MeshNodeLayoutAreas.MoreActionsClass)
                           && json.Contains(MeshNodeLayoutAreas.MoreActionsItemClass)
                           && json.Contains($"/{Person}/files"),
                "the owner's home draws the More dropdown beside the page, holding the entries a protected root keeps (Files)",
                TestContext.Current.CancellationToken);

        foreach (var area in new[] { "move", "copy", "delete" })
            page.Should().NotContain($"/{Person}/{area}",
                $"'{area}' is suppressed on a user's home - it would act on the whole partition");
    }

    [Fact(Timeout = 60000)]
    public async Task TheActionsRow_SitsBesideTheBody_NotInsideIt()
        => await Render(Person, UserActivityLayoutAreas.ActivityArea)
            .Should().Within(Budget)
            .Match(json => json.Contains(UserActivityLayoutAreas.HomeActionsArea.ToLowerInvariant())
                           && json.Contains(UserActivityLayoutAreas.HomeBodyArea.ToLowerInvariant()),
                "the row is a sub-area of its own, so the body's rebuild on every edit does not re-create it",
                TestContext.Current.CancellationToken);

    [Fact(Timeout = 60000)]
    public async Task AnOrdinaryPage_StillDrawsMore()
        => await Render(Page, MeshNodeLayoutAreas.OverviewArea)
            .Should().Within(Budget)
            .Match(json => json.Contains(MeshNodeLayoutAreas.MoreActionsClass),
                "the control: the same probe finds More on a standard node page",
                TestContext.Current.CancellationToken);
}
