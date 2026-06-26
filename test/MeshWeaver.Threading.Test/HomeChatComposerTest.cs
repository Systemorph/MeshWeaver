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
/// Pins the User-Page home chat composer contract: the per-user <c>{user}/Chat</c> node is
/// materialised on demand by the User dashboard (robust create-if-absent), and its default
/// <c>Overview</c> area returns the SAME <see cref="ThreadChatControl"/> the side panel mounts —
/// so the home composer is 1:1 the side-panel composer. See <see cref="ChatNodeType"/> and
/// <c>UserActivityLayoutAreas</c>.
/// </summary>
public class HomeChatComposerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // The signed-in test user (TestUsers.Admin) is "Roland"; the User node is at "User/Roland",
    // the owner's content partition is "Roland", so the home chat node is "Roland/Chat".
    private const string OwnerId = "Roland";
    private static readonly Address UserNodeAddress = new($"User/{OwnerId}");
    private static readonly Address ChatNodeAddress = new($"{OwnerId}/Chat");
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
    /// Rendering the User node's <c>Activity</c> area for the owner CREATES <c>{owner}/Chat</c> when it
    /// is absent — the robust "create a new one if it does not exist in init" path, for new and
    /// pre-existing users alike (no onboarding back-fill).
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Activity_CreatesHomeChatNode_WhenAbsent()
    {
        // The viewer must be the owner for the dashboard (vs the visitor profile) — DevLogin already
        // set this on the mesh AccessService; assert it so the test's intent is explicit.
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
        var dashJson = dashboard.Value.ToString();
        Output.WriteLine($"dashboard owner={dashJson.Contains("Welcome back")} visitor={dashJson.Contains("UserProfileControl")}");
        dashJson.Should().Contain("Welcome back", "the viewer is the owner, so the owner dashboard renders");

        // The dashboard's ensure-create should have materialised {owner}/Chat — poll until it appears
        // (GetMeshNodeStream errors on an absent node, so swallow that while polling).
        var chatNode = await PollForNode(workspace, ChatNodePath, TimeSpan.FromSeconds(30));
        chatNode.Should().NotBeNull("rendering the owner dashboard ensure-creates the home chat node");
        chatNode!.NodeType.Should().Be(ChatNodeType.NodeType, "the home chat node is a Chat node");
    }

    /// <summary>
    /// The <c>{user}/Chat</c> node's default <c>Overview</c> area returns the side-panel
    /// <see cref="ThreadChatControl"/> — proving the home composer is the same control, not a parallel
    /// rendering. (No thread path → new-chat composer; sending starts a thread.)
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ChatNode_Overview_ReturnsSidePanelThreadChatControl()
    {
        var client = GetClient();

        // Create the home chat node directly (independent of the dashboard), then render its Overview.
        var create = await client.Observe(new CreateNodeRequest(
                MeshNode.FromPath(ChatNodePath) with { NodeType = ChatNodeType.NodeType, Name = "Chat" }),
                o => o.WithTarget(Mesh.Address))
            .Should().Within(30.Seconds()).Emit();
        create.Message.Success.Should().BeTrue(create.Message.Error ?? "");

        await client.Observe(new PingRequest(), o => o.WithTarget(ChatNodeAddress)).Should().Within(30.Seconds()).Emit();

        var workspace = client.GetWorkspace();
        var overview = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            ChatNodeAddress, new LayoutAreaReference(ChatNodeType.OverviewArea));

        // Wait past "Building layout..." for the emission that carries the rendered control.
        var rendered = await overview
            .Where(c => c.Value.ToString().Contains(nameof(ThreadChatControl)))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(30)).ToTask();

        rendered.Value.ToString().Should().Contain(nameof(ThreadChatControl),
            "the home chat Overview must return the SAME control the side panel uses");
    }

    /// <summary>
    /// Polls <c>GetMeshNodeStream(path)</c> until the node exists or the deadline elapses, swallowing the
    /// "No node found" error a not-yet-created node raises. Returns null on timeout.
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
