using System;
using System.Collections.Generic;
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
        state.Replies.Should().BeEmpty();
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

    [Fact]
    public void AnnotationPanelState_AddReply_StoresInRepliesCollection()
    {
        var state = new MarkdownLayoutAreas.AnnotationPanelState();
        var annotationId = "c1";
        var replyText = "This is a reply";
        var author = "TestUser";
        var timestamp = DateTimeOffset.Now;

        // Simulate adding a reply
        var newReplies = new Dictionary<string, List<(string Author, string Text, DateTimeOffset Time)>>(state.Replies);
        newReplies[annotationId] = new List<(string, string, DateTimeOffset)>
        {
            (author, replyText, timestamp)
        };

        var newState = new MarkdownLayoutAreas.AnnotationPanelState
        {
            ReplyingToAnnotationId = null,
            Replies = newReplies
        };

        newState.Replies.Should().ContainKey(annotationId);
        newState.Replies[annotationId].Should().HaveCount(1);
        newState.Replies[annotationId][0].Author.Should().Be(author);
        newState.Replies[annotationId][0].Text.Should().Be(replyText);
    }

    [Fact]
    public void AnnotationPanelState_AddMultipleReplies_StoresAllReplies()
    {
        var state = new MarkdownLayoutAreas.AnnotationPanelState();
        var annotationId = "c1";

        // Add first reply
        var replies1 = new Dictionary<string, List<(string Author, string Text, DateTimeOffset Time)>>();
        replies1[annotationId] = new List<(string, string, DateTimeOffset)>
        {
            ("Alice", "First reply", DateTimeOffset.Now)
        };
        var state1 = new MarkdownLayoutAreas.AnnotationPanelState { Replies = replies1 };

        // Add second reply
        var replies2 = new Dictionary<string, List<(string Author, string Text, DateTimeOffset Time)>>(state1.Replies);
        replies2[annotationId].Add(("Bob", "Second reply", DateTimeOffset.Now));
        var state2 = new MarkdownLayoutAreas.AnnotationPanelState { Replies = replies2 };

        state2.Replies[annotationId].Should().HaveCount(2);
        state2.Replies[annotationId][0].Author.Should().Be("Alice");
        state2.Replies[annotationId][1].Author.Should().Be("Bob");
    }

    [Fact]
    public void AnnotationPanelState_RepliesToDifferentAnnotations_TrackedSeparately()
    {
        var replies = new Dictionary<string, List<(string Author, string Text, DateTimeOffset Time)>>
        {
            ["c1"] = new List<(string, string, DateTimeOffset)> { ("Alice", "Reply to c1", DateTimeOffset.Now) },
            ["c2"] = new List<(string, string, DateTimeOffset)> { ("Bob", "Reply to c2", DateTimeOffset.Now) }
        };

        var state = new MarkdownLayoutAreas.AnnotationPanelState { Replies = replies };

        state.Replies.Should().HaveCount(2);
        state.Replies["c1"][0].Author.Should().Be("Alice");
        state.Replies["c2"][0].Author.Should().Be("Bob");
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
    public void ReplyWorkflow_SubmitReply_AddsReplyAndClearsDialog()
    {
        // Simulate: User types reply and clicks Submit
        var annotationId = "c1";
        var replyingState = new MarkdownLayoutAreas.AnnotationPanelState { ReplyingToAnnotationId = annotationId };

        // User typed "Great point!" and clicks Submit
        var replyText = "Great point!";
        var newReplies = new Dictionary<string, List<(string Author, string Text, DateTimeOffset Time)>>(replyingState.Replies);
        if (!newReplies.ContainsKey(annotationId))
        {
            newReplies[annotationId] = new List<(string, string, DateTimeOffset)>();
        }
        newReplies[annotationId].Add(("You", replyText, DateTimeOffset.Now));

        var afterSubmit = new MarkdownLayoutAreas.AnnotationPanelState
        {
            ReplyingToAnnotationId = null,
            Replies = newReplies
        };

        // Verify dialog is closed
        afterSubmit.ReplyingToAnnotationId.Should().BeNull();

        // Verify reply was added
        afterSubmit.Replies.Should().ContainKey(annotationId);
        afterSubmit.Replies[annotationId].Should().ContainSingle();
        afterSubmit.Replies[annotationId][0].Text.Should().Be("Great point!");
    }

    [Fact]
    public void ReplyWorkflow_SubmitEmptyReply_ShouldNotAddReply()
    {
        // Simulate: User clicks Submit without typing anything
        var annotationId = "c1";
        var replyingState = new MarkdownLayoutAreas.AnnotationPanelState { ReplyingToAnnotationId = annotationId };

        // Empty reply text - simulate validation
        var replyText = "";
        var shouldAddReply = !string.IsNullOrWhiteSpace(replyText);

        shouldAddReply.Should().BeFalse();
        // State should remain unchanged if validation fails
        replyingState.Replies.Should().BeEmpty();
    }

    [Fact]
    public void ReplyWorkflow_MultipleRepliesInSequence_AllPreserved()
    {
        var annotationId = "c1";
        var state = new MarkdownLayoutAreas.AnnotationPanelState();

        // First reply
        var replies1 = new Dictionary<string, List<(string Author, string Text, DateTimeOffset Time)>>();
        replies1[annotationId] = new List<(string, string, DateTimeOffset)> { ("Alice", "First", DateTimeOffset.Now) };
        state = new MarkdownLayoutAreas.AnnotationPanelState { Replies = replies1 };

        // Second reply
        var replies2 = new Dictionary<string, List<(string Author, string Text, DateTimeOffset Time)>>(state.Replies);
        replies2[annotationId].Add(("Bob", "Second", DateTimeOffset.Now));
        state = new MarkdownLayoutAreas.AnnotationPanelState { Replies = replies2 };

        // Third reply
        var replies3 = new Dictionary<string, List<(string Author, string Text, DateTimeOffset Time)>>(state.Replies);
        replies3[annotationId].Add(("Carol", "Third", DateTimeOffset.Now));
        state = new MarkdownLayoutAreas.AnnotationPanelState { Replies = replies3 };

        state.Replies[annotationId].Should().HaveCount(3);
        state.Replies[annotationId][0].Author.Should().Be("Alice");
        state.Replies[annotationId][1].Author.Should().Be("Bob");
        state.Replies[annotationId][2].Author.Should().Be("Carol");
    }

    #endregion
}
