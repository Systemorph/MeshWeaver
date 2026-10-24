using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="AppNodeType.NotificationPreferencesArea"/> — the per-installed-app area the
/// Notifications settings tab hands each row of its data-bound app list to (<c>WithItemArea</c>),
/// so the tab reads nothing on its own hub (Doc/GUI/DataBinding, "Templates first, data later").
/// The area owns what the tab used to do inline per app: create the person's preference node on
/// first view with the dispatcher's default, then bind the standard editor to it.
/// </summary>
public class InstalledAppNotificationPreferencesAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Person = "napref_person";
    private const string App = "Chess";

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [Theory]
    [InlineData("alice/_App/Chess", true, "alice", "Chess")]
    [InlineData("org/alice/_App/parties-app", true, "org/alice", "parties-app")]
    [InlineData("alice/_App/a/b", false, "", "")]
    [InlineData("alice/_App/", false, "", "")]
    [InlineData("_App/Chess", false, "", "")]
    [InlineData("alice/Apps/Chess", false, "", "")]
    [InlineData(null, false, "", "")]
    public void TheRecordPath_NamesThePersonAndTheApp(string? path, bool parsed, string user, string app)
    {
        Assert.Equal(parsed, AppNodeType.TryParsePath(path, out var u, out var a));
        Assert.Equal(user, u);
        Assert.Equal(app, a);
    }

    [Fact]
    public void TheControl_IsTheAppsCard_AboveTheEditorBoundToThePreferenceNode()
    {
        var appPath = AppNodeType.PathFor("alice", App);
        var preferencePath = NotificationAppPreferencePaths.PathFor("alice", App);

        var stack = Assert.IsType<StackControl>(AppNodeType.NotificationPreferencesFor(appPath, preferencePath));
        var controls = Views(stack);
        Assert.Equal(2, controls.Count);
        var card = Assert.IsType<MeshNodeThumbnailControl>(controls[0]);
        Assert.Equal(appPath, card.NodePath);
        var editor = Assert.IsType<MeshNodeContentEditorControl>(controls[1]);
        Assert.Equal(preferencePath, editor.NodePath);
    }

    /// <summary>
    /// Rendered on a real installed-app record, the area creates the person's preference node —
    /// which is what makes opening the tab change no delivery while still giving the editor a node
    /// to bind to.
    /// </summary>
    [Fact(Timeout = 90000)]
    public async Task RenderingTheArea_SeedsThePersonsPreferenceNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.ImpersonateAsSystem())
        {
            await mesh.CreateNode(new MeshNode(Person)
            {
                NodeType = "User",
                Name = Person,
                Content = new User { Email = $"{Person}@example.com", FullName = Person },
            }).Should().Emit(cancellationToken: ct);
            await mesh.CreateNode(new MeshNode(App, $"{Person}/{AppNodeType.UserNamespace}")
            {
                NodeType = AppNodeType.NodeType,
                Name = App,
                Content = new App { Plugin = App },
            }).Should().Emit(cancellationToken: ct);
        }

        var appPath = AppNodeType.PathFor(Person, App);
        await GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(appPath), new LayoutAreaReference(AppNodeType.NotificationPreferencesArea))
            .GetControlStream(AppNodeType.NotificationPreferencesArea)
            .Where(c => c is StackControl)
            .FirstAsync()
            .Should().Within(TestTimeouts.Convergence).Emit("the area binds the editor once the node exists", ct);

        var preferencePath = NotificationAppPreferencePaths.PathFor(Person, App);
        await Mesh.GetWorkspace().GetMeshNodeStream(preferencePath)
            .Where(n => n.NodeType == NotificationAppPreferenceNodeType.NodeType)
            .FirstAsync()
            .Should().Within(TestTimeouts.WriteConvergence).Emit("opening the area seeds the person's preference", ct);
    }

    /// <summary>A container's direct child controls (the <c>Views</c> property is non-public).</summary>
    private static System.Collections.Generic.List<UiControl> Views(UiControl root)
    {
        for (var type = root.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty("Views",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly);
            if (property?.GetValue(root) is System.Collections.IEnumerable views)
                return views.OfType<UiControl>().ToList();
        }
        return [];
    }
}
