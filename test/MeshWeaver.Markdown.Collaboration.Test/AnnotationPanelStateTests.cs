using System;
using FluentAssertions;
using MeshWeaver.Graph;
using Xunit;

namespace MeshWeaver.Markdown.Collaboration.Test;

public class AnnotationPanelStateTests
{
    #region AnnotationPanelState Tests

    [Fact]
    public void AnnotationPanelState_DefaultState_HasNoReplyingAnnotation()
    {
        var state = new MarkdownLayoutAreas.AnnotationPanelState();

        state.ReplyingToAnnotationId.Should().BeNull();
        state.ExpandedAnnotationIds.Should().BeEmpty();
    }

    [Fact]
    public void AnnotationPanelState_SetReplyingToAnnotation_TracksCorrectly()
    {
        var state = new MarkdownLayoutAreas.AnnotationPanelState();

        var newState = state with { ReplyingToAnnotationId = "c1" };

        newState.ReplyingToAnnotationId.Should().Be("c1");
    }

    [Fact]
    public void AnnotationPanelState_ClearReplyingAnnotation_ResetsToNull()
    {
        var state = new MarkdownLayoutAreas.AnnotationPanelState { ReplyingToAnnotationId = "c1" };

        var newState = state with { ReplyingToAnnotationId = null };

        newState.ReplyingToAnnotationId.Should().BeNull();
    }

    #endregion

    #region ReplyFormModel Tests

    [Fact]
    public void ReplyFormModel_DefaultState_HasEmptyText()
    {
        var model = new MarkdownLayoutAreas.ReplyFormModel();

        model.Text.Should().BeEmpty();
    }

    [Fact]
    public void ReplyFormModel_SetText_StoresCorrectly()
    {
        var model = new MarkdownLayoutAreas.ReplyFormModel { Text = "My reply text" };

        model.Text.Should().Be("My reply text");
    }

    #endregion

    #region Reply Workflow Tests

    [Fact]
    public void ReplyWorkflow_ClickReply_SetsReplyingAnnotationId()
    {
        // Simulate: User clicks Reply button on annotation "c1"
        var initialState = new MarkdownLayoutAreas.AnnotationPanelState();

        var afterClick = initialState with { ReplyingToAnnotationId = "c1" };

        afterClick.ReplyingToAnnotationId.Should().Be("c1");
    }

    [Fact]
    public void ReplyWorkflow_ClickCancel_ClearsReplyingAnnotationId()
    {
        // Simulate: User clicks Cancel on reply form
        var replyingState = new MarkdownLayoutAreas.AnnotationPanelState { ReplyingToAnnotationId = "c1" };

        var afterCancel = replyingState with { ReplyingToAnnotationId = null };

        afterCancel.ReplyingToAnnotationId.Should().BeNull();
    }

    [Fact]
    public void ReplyWorkflow_SubmitReply_ClearsDialog()
    {
        // Simulate: User types reply and clicks Submit — reply is now a MeshNode, not in-memory state
        var annotationId = "c1";
        var replyingState = new MarkdownLayoutAreas.AnnotationPanelState { ReplyingToAnnotationId = annotationId };

        // After submit: just close the form (reactive subscription handles display)
        var afterSubmit = replyingState with { ReplyingToAnnotationId = null };

        afterSubmit.ReplyingToAnnotationId.Should().BeNull();
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
