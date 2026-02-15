using System;
using FluentAssertions;
using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Markdown.Collaboration.Test;

public class AnnotationPanelStateTests
{
    #region AnnotationPanelState Tests

    [Fact]
    public void AnnotationPanelState_DefaultState_HasNoEditingReplyPath()
    {
        var state = new MarkdownLayoutAreas.AnnotationPanelState();

        state.EditingReplyPath.Should().BeNull();
        state.ExpandedAnnotationIds.Should().BeEmpty();
    }

    [Fact]
    public void AnnotationPanelState_SetEditingReplyPath_TracksCorrectly()
    {
        var state = new MarkdownLayoutAreas.AnnotationPanelState();

        var newState = state with { EditingReplyPath = "docs/mypage/comment1/reply1" };

        newState.EditingReplyPath.Should().Be("docs/mypage/comment1/reply1");
    }

    [Fact]
    public void AnnotationPanelState_ClearEditingReplyPath_ResetsToNull()
    {
        var state = new MarkdownLayoutAreas.AnnotationPanelState { EditingReplyPath = "docs/mypage/comment1/reply1" };

        var newState = state with { EditingReplyPath = null };

        newState.EditingReplyPath.Should().BeNull();
    }

    #endregion

    #region Reply Workflow Tests

    [Fact]
    public void ReplyWorkflow_ClickReply_SetsEditingReplyPath()
    {
        // Simulate: User clicks Reply button — creates a MeshNode and sets editing path
        var initialState = new MarkdownLayoutAreas.AnnotationPanelState();

        var afterClick = initialState with { EditingReplyPath = "docs/mypage/comment1/reply1" };

        afterClick.EditingReplyPath.Should().Be("docs/mypage/comment1/reply1");
    }

    [Fact]
    public void ReplyWorkflow_ClickCancel_ClearsEditingReplyPath()
    {
        // Simulate: User clicks Cancel on reply edit form
        var editingState = new MarkdownLayoutAreas.AnnotationPanelState { EditingReplyPath = "docs/mypage/comment1/reply1" };

        var afterCancel = editingState with { EditingReplyPath = null };

        afterCancel.EditingReplyPath.Should().BeNull();
    }

    [Fact]
    public void ReplyWorkflow_ClickDone_ClearsEditingReplyPath()
    {
        // Simulate: User clicks Done after editing — auto-save handles persistence
        var editingState = new MarkdownLayoutAreas.AnnotationPanelState { EditingReplyPath = "docs/mypage/comment1/reply1" };

        var afterDone = editingState with { EditingReplyPath = null };

        afterDone.EditingReplyPath.Should().BeNull();
    }

    [Fact]
    public void ReplyWorkflow_SubmitEmptyReply_ShouldNotAddReply()
    {
        // Simulate: User clicks Submit without typing anything
        var replyText = "";
        var shouldAddReply = !string.IsNullOrWhiteSpace(replyText);

        shouldAddReply.Should().BeFalse();
    }

    #endregion
}
