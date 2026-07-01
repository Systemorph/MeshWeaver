using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Pins the User-Page home chat composer contract: the composer region is a PURE layout area that
/// renders the SAME <see cref="ThreadChatControl"/> the side panel mounts for a new chat — hosted
/// INLINE on the already-alive user hub, with NO backing <c>{owner}/Chat</c> mesh node. There is
/// nothing to create, so the home composer can never 404 with "No node found at '{owner}/Chat'".
/// See <c>UserActivityLayoutAreas.ComposerAreaView</c>.
/// </summary>
public class HomeChatComposerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // The signed-in test user (TestUsers.Admin) is "Roland"; the User node is at "User/Roland",
    // the owner's content partition is "Roland" (so the legacy home chat node would have been "Roland/Chat").
    private const string OwnerId = "Roland";
    private static readonly Address UserNodeAddress = new($"User/{OwnerId}");
    private const string ChatNodePath = OwnerId + "/Chat";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddData().AddLayoutClient();
    }

    /// <summary>
    /// The User hub's "Composer" region returns the side-panel <see cref="ThreadChatControl"/> INLINE —
    /// no backing node, no route to <c>{owner}/Chat</c>. (No thread path → new-chat composer; sending
    /// starts a thread under the user's home.) This is the whole point of making it a layout area: the
    /// area resolves purely from the live user hub, with nothing to provision.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ComposerArea_ReturnsSidePanelThreadChatControl_Inline()
    {
        // The viewer is the owner — DevLogin already set this on the mesh AccessService; assert it so
        // the test's intent is explicit.
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetCircuitContext(TestUsers.Admin);

        var client = GetClient();
        await client.Observe(new PingRequest(), o => o.WithTarget(UserNodeAddress)).Should().Within(30.Seconds()).Emit();

        var workspace = client.GetWorkspace();
        var composer = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            UserNodeAddress, new LayoutAreaReference(UserActivityLayoutAreas.ComposerArea));

        // Wait past "Building layout..." for the emission that carries the rendered control.
        var rendered = await composer
            .Where(c => c.Value.ToString().Contains(nameof(ThreadChatControl)))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        rendered.Value.ToString().Should().Contain(nameof(ThreadChatControl),
            "the home composer must render the SAME control the side panel uses, inline on the user hub");
    }

    /// <summary>
    /// Rendering the owner dashboard renders the home page but creates NO <c>{owner}/Chat</c> node — the
    /// composer is a pure layout area now, so there is no on-demand node create to fail (nor to leave an
    /// inaccessible node behind). Confirms the "No node found at '{owner}/Chat'" failure class is gone.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Activity_RendersOwnerDashboard_AndCreatesNoChatNode()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetCircuitContext(TestUsers.Admin);

        var client = GetClient();
        await client.Observe(new PingRequest(), o => o.WithTarget(UserNodeAddress)).Should().Within(30.Seconds()).Emit();

        var workspace = client.GetWorkspace();
        var activity = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            UserNodeAddress, new LayoutAreaReference(UserActivityLayoutAreas.ActivityArea));

        // Wait past the "Building layout..." progress for the fully-rendered owner dashboard.
        var dashboard = await activity
            .Where(c => { var s = c.Value.ToString(); return s.Contains("Welcome back") || s.Contains("UserProfileControl"); })
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();
        dashboard.Value.ToString().Should().Contain("Welcome back",
            "the viewer is the owner, so the owner dashboard renders");

        // The composer is now a pure layout area — no {owner}/Chat node is ever materialised. Confirm the
        // path stays absent (GetMeshNodeStream OnErrors "No node found" for an absent node → null here).
        var chatNode = await PollForNode(workspace, ChatNodePath, TimeSpan.FromSeconds(3));
        chatNode.Should().BeNull("the home composer no longer materialises a {owner}/Chat node");
    }

    /// <summary>
    /// Polls <c>GetMeshNodeStream(path)</c> until the node exists or the deadline elapses, swallowing the
    /// "No node found" error an absent node raises. Returns null on timeout (i.e. the node never appeared).
    /// </summary>
    private static Task<MeshNode?> PollForNode(IWorkspace workspace, string path, TimeSpan timeout) =>
        Observable.Interval(TimeSpan.FromMilliseconds(250)).StartWith(0L)
            .SelectMany(_ => workspace.GetMeshNodeStream(path).Take(1)
                .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null)))
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(timeout)
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .ToTask();
}
