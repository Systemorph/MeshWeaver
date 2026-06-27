#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using MeshWeaver.Blazor.Portal.Chat;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Acme.Test;

/// <summary>
/// Pins the value equality of <see cref="MessageBubbleState"/> — the dedup key in
/// <c>ThreadChatView.UpdateMessageState</c>'s per-message <c>.Subscribe()</c>.
///
/// 🚨 Regression: the chat VANISHED "when pushing the output message". Each message-stream
/// emission deserializes FRESH <c>ToolCalls</c>/<c>UpdatedNodes</c> lists; the synthesized record
/// equality compared them by REFERENCE, so for the agent's output message (which carries tool
/// calls / node changes) <c>Equals(prev, newState)</c> was perpetually false → the dedup never
/// fired → <c>StateHasChanged</c> stormed on every (no-op) re-emission of the JsonElement message
/// stream → the Blazor circuit saturated and tore down. The fix compares those lists by SEQUENCE.
/// </summary>
public class MessageBubbleStateEqualityTest
{
    private static readonly DateTime Ts = new(2026, 6, 27, 8, 0, 0, DateTimeKind.Utc);

    private static MessageBubbleState Output(
        IReadOnlyList<ToolCallEntry>? toolCalls, IReadOnlyList<NodeChangeEntry>? nodes)
        => new("assistant", "Assistant", "gpt", Ts, "done", toolCalls, nodes, Status: "Completed");

    [Fact]
    public void IdenticalContent_DistinctListInstances_AreEqual()
    {
        // Exactly what two deserializations of the SAME message JSON produce: structurally
        // identical, reference-distinct lists. These MUST be equal or the dedup storms.
        var tc1 = new[] { new ToolCallEntry { Name = "Get", Arguments = "@Doc/Page", Result = "ok", Timestamp = Ts } };
        var tc2 = new[] { new ToolCallEntry { Name = "Get", Arguments = "@Doc/Page", Result = "ok", Timestamp = Ts } };
        var un1 = new[] { new NodeChangeEntry { Path = "Doc/Page", Operation = "Updated", VersionAfter = 5 } };
        var un2 = new[] { new NodeChangeEntry { Path = "Doc/Page", Operation = "Updated", VersionAfter = 5 } };

        var a = Output(tc1, un1);
        var b = Output(tc2, un2);

        Assert.NotSame(tc1, tc2);                          // distinct instances (as deserialization yields)
        Assert.Equal(a, b);                                // ...yet equal → dedup fires → no storm
        Assert.Equal(a.GetHashCode(), b.GetHashCode());    // hash consistent with Equals
    }

    [Fact]
    public void DifferentToolCallContent_AreNotEqual()
    {
        var a = Output(new[] { new ToolCallEntry { Name = "Get", Result = "ok", Timestamp = Ts } }, null);
        var b = Output(new[] { new ToolCallEntry { Name = "Get", Result = "DIFFERENT", Timestamp = Ts } }, null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void StreamingTextChange_IsNotEqual()
    {
        // A real change (streaming text grows) MUST still re-render.
        var a = new MessageBubbleState("assistant", "Assistant", "gpt", Ts, "partial", null, null);
        var b = new MessageBubbleState("assistant", "Assistant", "gpt", Ts, "partial more", null, null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BothNullLists_IdenticalScalars_AreEqual()
    {
        var a = new MessageBubbleState("user", "You", null, Ts, "hi", null, null);
        var b = new MessageBubbleState("user", "You", null, Ts, "hi", null, null);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void EmptyVsNullList_AreEqual()
    {
        // Absent vs empty tool-calls should not be treated as a change.
        var a = Output(Array.Empty<ToolCallEntry>(), null);
        var b = Output(null, null);
        Assert.Equal(a, b);
    }
}
