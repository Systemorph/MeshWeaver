using System.Text.Json;
using MeshWeaver.Maui.Abstractions;
using Xunit;

namespace MeshWeaver.Maui.Abstractions.Test;

/// <summary>
/// Projection of the thread/message JSON the native chat views bind to: the data-section
/// ThreadViewModel → which bubbles + status, and a ThreadMessage node's content → the bubble fields.
/// </summary>
public class MauiChatProjectionTest
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void ReadThreadViewModel_ExtractsBubblesAndStatus()
    {
        var vm = Json("""
        {
          "threadPath": "rb/Chat/t1",
          "messages": ["m1", "m2", "m3"],
          "pendingMessageTexts": ["queued one"],
          "isExecuting": true,
          "executionStatus": "Calling search_nodes..."
        }
        """);

        var state = MauiChatProjection.ReadThreadViewModel(vm);

        state.ThreadPath.Should().Be("rb/Chat/t1");
        state.MessageIds.Should().HaveCount(3);
        state.MessageIds.Should().Contain("m2");
        state.PendingTexts.Should().ContainSingle();
        state.PendingTexts[0].Should().Be("queued one");
        state.IsExecuting.Should().BeTrue();
        state.ExecutionStatus.Should().Be("Calling search_nodes...");
    }

    [Fact]
    public void ReadThreadViewModel_MissingFields_AreSafeDefaults()
    {
        var state = MauiChatProjection.ReadThreadViewModel(Json("""{ "threadPath": "x" }"""));

        state.MessageIds.Should().BeEmpty();
        state.PendingTexts.Should().BeEmpty();
        state.IsExecuting.Should().BeFalse();
        state.ExecutionStatus.Should().BeNull();
    }

    [Fact]
    public void ReadMessage_UserCell()
    {
        var msg = Json("""{ "role": "user", "text": "hello", "status": "Completed", "timestamp": "2026-06-29T10:00:00Z" }""");

        var bubble = MauiChatProjection.ReadMessage(msg);

        bubble.Role.Should().Be("user");
        bubble.IsUser.Should().BeTrue();
        bubble.Text.Should().Be("hello");
        bubble.IsStreaming.Should().BeFalse();
        bubble.Timestamp.Should().NotBeNull();
    }

    [Fact]
    public void ReadMessage_StreamingAssistantCell_WithAgentNameFallback()
    {
        var msg = Json("""{ "role": "assistant", "text": "thinking", "status": "Streaming", "agentName": "Navigator", "modelName": "claude-sonnet-4-6" }""");

        var bubble = MauiChatProjection.ReadMessage(msg);

        bubble.IsUser.Should().BeFalse();
        bubble.IsStreaming.Should().BeTrue();
        bubble.AuthorName.Should().Be("Navigator"); // authorName missing → agentName
        bubble.ModelName.Should().Be("claude-sonnet-4-6");
    }

    [Fact]
    public void ReadMessage_AuthorNamePreferredOverAgentName()
    {
        var msg = Json("""{ "role": "assistant", "text": "x", "authorName": "You-set", "agentName": "Navigator" }""");
        MauiChatProjection.ReadMessage(msg).AuthorName.Should().Be("You-set");
    }

    [Fact]
    public void JsonReaders_TolerateWrongKinds()
    {
        var e = Json("""{ "s": 5, "b": "nope", "arr": "x" }""");
        MauiChatProjection.GetString(e, "s").Should().BeNull();   // number, not string
        MauiChatProjection.GetBool(e, "b").Should().BeFalse();    // string, not true
        MauiChatProjection.GetStringArray(e, "arr").Should().BeEmpty(); // string, not array
        MauiChatProjection.GetString(e, "missing").Should().BeNull();
    }
}
