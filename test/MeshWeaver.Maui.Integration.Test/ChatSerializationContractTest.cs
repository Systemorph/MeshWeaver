using System.Text.Json;
using MeshWeaver.AI;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Maui.Abstractions;
using MeshWeaver.Mesh;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Maui.Integration.Test;

/// <summary>
/// Tier-2 contract test: the native chat views project a thread/message via <see cref="MauiChatProjection"/>
/// off JSON. Tier-1 tests that projection against hand-built JSON; THIS asserts the framework's REAL
/// serialization of <see cref="MeshThread"/> / <see cref="ThreadMessage"/> through the live hub
/// <c>JsonSerializerOptions</c> (camelCase, enum-as-string, computed getters) produces exactly the keys +
/// values the projection reads — so a casing/enum-shape change that would silently break the native chat
/// is caught here, not at runtime on the device.
/// </summary>
public class ChatSerializationContractTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).AddAI().AddSampleUsers();

    // The live hub serializer the layout-area data section + node streams use.
    private JsonSerializerOptions Options => Mesh.JsonSerializerOptions;

    [Fact]
    public void MeshThread_SerializesToTheKeysThreadChatViewProjects()
    {
        var thread = new MeshThread
        {
            Messages = ["m1", "m2", "m3"],
            Status = ThreadExecutionStatus.Executing,
            ExecutionStatus = "Calling search_nodes...",
        };

        var state = MauiChatProjection.ReadThreadViewModel(JsonSerializer.SerializeToElement(thread, Options));

        state.MessageIds.Should().HaveCount(3);
        state.MessageIds.Should().Contain("m2");
        state.IsExecuting.Should().BeTrue();   // Status=Executing → computed isExecuting → serialized true
        state.ExecutionStatus.Should().Be("Calling search_nodes...");
    }

    [Fact]
    public void MeshThread_Idle_IsNotExecuting()
    {
        var thread = new MeshThread { Messages = ["m1"], Status = ThreadExecutionStatus.Idle };
        var state = MauiChatProjection.ReadThreadViewModel(JsonSerializer.SerializeToElement(thread, Options));
        state.IsExecuting.Should().BeFalse();
    }

    [Fact]
    public void ThreadMessage_StreamingAssistant_ProjectsToBubble()
    {
        var msg = new ThreadMessage
        {
            Role = "assistant",
            Text = "thinking…",
            Status = ThreadMessageStatus.Streaming,
            AgentName = "Navigator",
            ModelName = "claude-sonnet-4-6",
            Timestamp = new DateTime(2026, 6, 29, 10, 0, 0, DateTimeKind.Utc),
        };

        var bubble = MauiChatProjection.ReadMessage(JsonSerializer.SerializeToElement(msg, Options));

        bubble.Role.Should().Be("assistant");
        bubble.IsUser.Should().BeFalse();
        bubble.Text.Should().Be("thinking…");
        bubble.IsStreaming.Should().BeTrue();          // Status=Streaming → serialized "Streaming"
        bubble.AuthorName.Should().Be("Navigator");     // authorName null → agentName fallback
        bubble.ModelName.Should().Be("claude-sonnet-4-6");
        bubble.Timestamp.Should().NotBeNull();
    }

    [Fact]
    public void ThreadMessage_UserCompleted_NotStreaming()
    {
        var msg = new ThreadMessage { Role = "user", Text = "hi", Status = ThreadMessageStatus.Completed };
        var bubble = MauiChatProjection.ReadMessage(JsonSerializer.SerializeToElement(msg, Options));
        bubble.IsUser.Should().BeTrue();
        bubble.Text.Should().Be("hi");
        bubble.IsStreaming.Should().BeFalse();
    }
}
