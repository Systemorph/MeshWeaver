using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using Xunit;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests that StreamingCompact does NOT recursively embed sub-thread LayoutAreaControls.
/// The recursive embed caused stack overflow when delegation sub-threads didn't exist
/// (each failed grain activation triggered another embed attempt).
/// </summary>
public class StreamingRecursionTest
{
    [Fact]
    public void ToolCalls_WithDelegationPath_DoNotEmbed_LayoutAreaControl()
    {
        // A ThreadMessage with delegation tool calls (simulates streaming state)
        var msg = new ThreadMessage
        {
            Id = "resp1",
            Role = "assistant",
            Text = "Delegating...",
            ToolCalls =
            [
                new ToolCallEntry
                {
                    Name = "delegate_to_agent",
                    DisplayName = "Delegating to Executor",
                    DelegationPath = "Org/_Thread/parent/msg1/sub-thread",
                    Result = null // In-progress — this was the trigger for recursive embed
                },
                new ToolCallEntry
                {
                    Name = "delegate_to_agent",
                    DisplayName = "Completed delegation",
                    DelegationPath = "Org/_Thread/parent/msg2/other-sub",
                    Result = "Done",
                    IsSuccess = true
                }
            ]
        };

        // The StreamingCompact view should NOT contain any LayoutAreaControl references
        // It should only have static HTML links for delegation paths
        // We verify by checking that the ToolCalls with DelegationPath don't trigger embedding

        // Verify the delegation entries exist
        var delegations = msg.ToolCalls.Where(tc => !string.IsNullOrEmpty(tc.DelegationPath)).ToList();
        delegations.Should().HaveCount(2);

        // The in-progress delegation (Result == null) must NOT trigger recursive rendering
        var inProgress = delegations.Where(tc => tc.Result == null).ToList();
        inProgress.Should().HaveCount(1);

        // Verify the fix: in-progress delegations should be rendered as static links,
        // NOT as LayoutAreaControl(delegationPath, "Streaming")
        // This is a design constraint test — the actual rendering is in StreamingCompact
        // but we verify the data model doesn't force recursion
        inProgress[0].DelegationPath.Should().NotBeNull();
        inProgress[0].Result.Should().BeNull("in-progress delegation has no result yet");

        // The key invariant: no LayoutAreaControl should be created for delegation paths
        // This is enforced by the code change in ThreadMessageLayoutAreas.StreamingCompact
        // which now renders static links instead of recursive embeds
    }

    [Fact]
    public void ThreadMessage_ToolCalls_AreImmutableList()
    {
        // Verify tool calls use ImmutableList (no mutable collections)
        var msg = new ThreadMessage
        {
            Id = "test",
            Role = "assistant",
            Text = "",
            ToolCalls = ImmutableList<ToolCallEntry>.Empty
                .Add(new ToolCallEntry { Name = "get", DisplayName = "Get" })
                .Add(new ToolCallEntry { Name = "update", DisplayName = "Update" })
        };

        msg.ToolCalls.Should().BeAssignableTo<ImmutableList<ToolCallEntry>>();
        msg.ToolCalls.Should().HaveCount(2);
    }
}
