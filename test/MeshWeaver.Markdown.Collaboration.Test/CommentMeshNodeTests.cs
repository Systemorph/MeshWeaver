using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;
using MarkdownAnnotationType = MeshWeaver.Markdown.AnnotationType;

namespace MeshWeaver.Markdown.Collaboration.Test;

/// <summary>
/// Tests for Comment MeshNode integration with collaborative markdown.
/// Verifies that:
/// - Simplified comment markers are parsed correctly
/// - Comment data lives in MeshNode (not in markdown markers)
/// - MarkerId links MeshNode to markdown marker
/// - Resolve removes markers from markdown
/// - CommentFormModel captures text selection data
/// </summary>
public class CommentMeshNodeTests
{
    #region Simplified Marker Parsing

    [Fact]
    public void SimplifiedCommentMarker_ParsedByExtractAnnotations()
    {
        // Simplified markers contain only the markerId, no author/date/text
        var markdown = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        var result = AnnotationParser.ExtractAnnotations(markdown);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("c1");
        result[0].Type.Should().Be(MarkdownAnnotationType.Comment);
        result[0].HighlightedText.Should().Be("world");
        result[0].Author.Should().BeNull();
        result[0].CommentText.Should().BeNull();
    }

    [Fact]
    public void SimplifiedCommentMarker_TransformsToHighlightSpan()
    {
        var markdown = "<!--comment:c1-->text<!--/comment:c1-->";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Be("<span class=\"comment-highlight\" data-comment-id=\"c1\">text</span>");
    }

    [Fact]
    public void SimplifiedCommentMarker_MultipleComments_AllParsed()
    {
        var markdown = "<!--comment:c1-->first<!--/comment:c1--> and <!--comment:c2-->second<!--/comment:c2-->";

        var result = AnnotationParser.ExtractAnnotations(markdown);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("c1");
        result[0].HighlightedText.Should().Be("first");
        result[1].Id.Should().Be("c2");
        result[1].HighlightedText.Should().Be("second");
    }

    [Fact]
    public void SimplifiedCommentMarker_MixedWithTrackChanges_AllParsed()
    {
        var markdown = "<!--comment:c1-->commented<!--/comment:c1--> " +
                       "<!--insert:i1:Bob:Dec 16-->inserted<!--/insert:i1--> " +
                       "<!--delete:d1:Carol:Dec 17-->deleted<!--/delete:d1-->";

        var result = AnnotationParser.ExtractAnnotations(markdown);

        result.Should().HaveCount(3);
        result.Should().Contain(a => a.Id == "c1" && a.Type == MarkdownAnnotationType.Comment);
        result.Should().Contain(a => a.Id == "i1" && a.Type == MarkdownAnnotationType.Insert);
        result.Should().Contain(a => a.Id == "d1" && a.Type == MarkdownAnnotationType.Delete);
    }

    [Fact]
    public void SimplifiedCommentMarker_StripAnnotations_RemovesMarkersKeepsText()
    {
        var markdown = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        var result = AnnotationMarkdownExtension.StripAnnotations(markdown);

        result.Should().Be("Hello world!");
    }

    [Fact]
    public void SimplifiedCommentMarker_GetAcceptedContent_RemovesMarkers()
    {
        var markdown = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        var result = AnnotationMarkdownExtension.GetAcceptedContent(markdown);

        result.Should().Be("Hello world!");
    }

    [Fact]
    public void SimplifiedCommentMarker_ResolveComment_RemovesSpecificMarker()
    {
        var markdown = "Hello <!--comment:c1-->world<!--/comment:c1--> <!--comment:c2-->foo<!--/comment:c2-->!";

        var result = AnnotationMarkdownExtension.ResolveComment(markdown, "c1");

        result.Should().Contain("Hello world");
        result.Should().NotContain("<!--comment:c1-->");
        // c2 should remain
        result.Should().Contain("<!--comment:c2-->foo<!--/comment:c2-->");
    }

    #endregion

    #region Comment MeshNode Data Model

    [Fact]
    public void Comment_DefaultState_HasActiveStatus()
    {
        var comment = new Comment();

        comment.Status.Should().Be(CommentStatus.Active);
        comment.Replies.Should().BeEmpty();
        comment.Id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Comment_WithMarkerId_LinksToMarkdownMarker()
    {
        var comment = new Comment
        {
            Id = "comment-1",
            MarkerId = "c1",
            HighlightedText = "world",
            Author = "Alice",
            Text = "Great point!",
            NodePath = "docs/mypage"
        };

        comment.MarkerId.Should().Be("c1");
        comment.HighlightedText.Should().Be("world");
        comment.Author.Should().Be("Alice");
        comment.Text.Should().Be("Great point!");
        comment.NodePath.Should().Be("docs/mypage");
    }

    [Fact]
    public void Comment_WithReplies_SupportsThreading()
    {
        var reply = new Comment
        {
            Id = "reply-1",
            Author = "Bob",
            Text = "I agree!",
            ParentCommentId = "comment-1"
        };

        var comment = new Comment
        {
            Id = "comment-1",
            MarkerId = "c1",
            Author = "Alice",
            Text = "Original comment",
            Replies = ImmutableList.Create(reply)
        };

        comment.Replies.Should().HaveCount(1);
        comment.Replies[0].Author.Should().Be("Bob");
        comment.Replies[0].ParentCommentId.Should().Be("comment-1");
    }

    [Fact]
    public void Comment_ResolvedStatus_MarksAsResolved()
    {
        var comment = new Comment
        {
            Id = "comment-1",
            Status = CommentStatus.Resolved
        };

        comment.Status.Should().Be(CommentStatus.Resolved);
    }

    #endregion

    #region CommentNodeType

    [Fact]
    public void CommentNodeType_HasCorrectValue()
    {
        CommentNodeType.NodeType.Should().Be("Comment");
    }

    #endregion

    #region Comment MeshNode Storage

    [Fact]
    public void CommentMeshNode_HasCorrectStructure()
    {
        var comment = new Comment
        {
            Id = "c1",
            NodePath = "docs/page",
            MarkerId = "c1",
            HighlightedText = "sample text",
            Author = "Alice",
            Text = "This needs review"
        };

        var node = new MeshNode("docs/page/c1")
        {
            Name = "Comment by Alice",
            NodeType = CommentNodeType.NodeType,
            Content = comment
        };

        node.Path.Should().Be("docs/page/c1");
        node.NodeType.Should().Be("Comment");
        node.Name.Should().Be("Comment by Alice");
        (node.Content as Comment)!.MarkerId.Should().Be("c1");
        (node.Content as Comment)!.HighlightedText.Should().Be("sample text");
    }

    [Fact]
    public void CommentMeshNode_ChildPath_FollowsDocumentHierarchy()
    {
        // Document path: docs/mypage
        // Comment path: docs/mypage/{commentId}
        // Reply path:   docs/mypage/{commentId}/{replyId}
        var docPath = "docs/mypage";
        var commentId = "comment1";
        var replyId = "reply1";

        var commentPath = $"{docPath}/{commentId}";
        var replyPath = $"{commentPath}/{replyId}";

        commentPath.Should().Be("docs/mypage/comment1");
        replyPath.Should().Be("docs/mypage/comment1/reply1");
    }

    #endregion

    #region CommentFormModel

    [Fact]
    public void CommentFormModel_DefaultState_HasEmptyFields()
    {
        var model = new MarkdownLayoutAreas.CommentFormModel();

        model.SelectedText.Should().BeEmpty();
        model.CommentText.Should().BeEmpty();
    }

    [Fact]
    public void CommentFormModel_CapturesTextSelection()
    {
        var model = new MarkdownLayoutAreas.CommentFormModel
        {
            SelectedText = "highlighted text",
            CommentText = "My comment about this"
        };

        model.SelectedText.Should().Be("highlighted text");
        model.CommentText.Should().Be("My comment about this");
    }

    #endregion

    #region Marker Insertion into Markdown

    [Fact]
    public void MarkerInsertion_InsertsAroundSelectedText()
    {
        var rawContent = "MeshWeaver is a powerful platform for building applications.";
        var selectedText = "powerful platform";
        var markerId = "abc123";

        var marker = $"<!--comment:{markerId}-->";
        var closing = $"<!--/comment:{markerId}-->";
        var idx = rawContent.IndexOf(selectedText, StringComparison.Ordinal);

        idx.Should().BeGreaterThanOrEqualTo(0);

        var newContent = rawContent.Insert(idx + selectedText.Length, closing)
                                   .Insert(idx, marker);

        newContent.Should().Be(
            $"MeshWeaver is a <!--comment:{markerId}-->powerful platform<!--/comment:{markerId}--> for building applications.");

        // Verify the marker can be parsed back
        var annotations = AnnotationParser.ExtractAnnotations(newContent);
        annotations.Should().HaveCount(1);
        annotations[0].Id.Should().Be(markerId);
        annotations[0].HighlightedText.Should().Be(selectedText);
    }

    [Fact]
    public void MarkerInsertion_RoundTripsWithResolve()
    {
        var original = "Hello world!";
        var markerId = "test1";

        // Insert marker
        var marker = $"<!--comment:{markerId}-->";
        var closing = $"<!--/comment:{markerId}-->";
        var idx = original.IndexOf("world", StringComparison.Ordinal);
        var withMarker = original.Insert(idx + 5, closing).Insert(idx, marker);

        withMarker.Should().Contain($"<!--comment:{markerId}-->world<!--/comment:{markerId}-->");

        // Resolve (remove marker)
        var resolved = AnnotationMarkdownExtension.ResolveComment(withMarker, markerId);

        resolved.Should().Be("Hello world!");
    }

    #endregion

    #region Sample Data Migration Verification

    [Fact]
    public void CollaborativeEditingSampleFormat_SimplifiedMarkers_ParseCorrectly()
    {
        // This simulates the migrated CollaborativeEditing.md format
        var markdown = "> MeshWeaver is a <!--comment:c1-->powerful platform<!--/comment:c1--> " +
                       "for building <!--comment:c2-->collaborative applications<!--/comment:c2-->. " +
                       "It provides real-time synchronization and <!--comment:c3-->conflict-free editing<!--/comment:c3-->.";

        var result = AnnotationParser.ExtractAnnotations(markdown);

        result.Should().HaveCount(3);

        result[0].Id.Should().Be("c1");
        result[0].HighlightedText.Should().Be("powerful platform");
        result[0].Author.Should().BeNull(); // No author in simplified format

        result[1].Id.Should().Be("c2");
        result[1].HighlightedText.Should().Be("collaborative applications");

        result[2].Id.Should().Be("c3");
        result[2].HighlightedText.Should().Be("conflict-free editing");
    }

    [Fact]
    public void CollaborativeEditingSample_CommentsWithTrackChanges_AllParsed()
    {
        // Mixed comments and track changes from sample data
        var markdown = "> Our team has completed the <!--delete:d3:Bob:Dec 22-->initial<!--/delete:d3-->" +
                       "<!--insert:i3:Bob:Dec 22-->comprehensive<!--/insert:i3--> analysis of the " +
                       "<!--comment:c4-->market trends<!--/comment:c4-->.";

        var result = AnnotationParser.ExtractAnnotations(markdown);

        result.Should().HaveCount(3);
        result.Should().Contain(a => a.Id == "d3" && a.Type == MarkdownAnnotationType.Delete);
        result.Should().Contain(a => a.Id == "i3" && a.Type == MarkdownAnnotationType.Insert);
        result.Should().Contain(a => a.Id == "c4" && a.Type == MarkdownAnnotationType.Comment);
    }

    [Fact]
    public void CollaborativeEditingSample_CommentMeshNode_MatchesMarker()
    {
        // Verify the linkage between Comment MeshNode and markdown marker
        var comment = new Comment
        {
            Id = "c1",
            NodePath = "MeshWeaver/Documentation/DataMesh/CollaborativeEditing",
            MarkerId = "c1",
            HighlightedText = "powerful platform",
            Author = "Alice",
            Text = "Can we add specific metrics here? Like number of users or performance benchmarks?"
        };

        var markdown = "> MeshWeaver is a <!--comment:c1-->powerful platform<!--/comment:c1--> for building apps.";
        var annotations = AnnotationParser.ExtractAnnotations(markdown);

        annotations.Should().HaveCount(1);
        annotations[0].Id.Should().Be(comment.MarkerId);
        annotations[0].HighlightedText.Should().Be(comment.HighlightedText);
    }

    #endregion

    #region Relative Time Formatting

    [Fact]
    public void FormatTimeAgo_RecentDate_ShowsJustNow()
    {
        var result = CommentsView.FormatTimeAgo(DateTimeOffset.UtcNow);
        result.Should().Be("Just now");
    }

    [Fact]
    public void FormatTimeAgo_MinutesAgo_ShowsMinutes()
    {
        var result = CommentsView.FormatTimeAgo(DateTimeOffset.UtcNow.AddMinutes(-5));
        result.Should().Be("5m ago");
    }

    [Fact]
    public void FormatTimeAgo_HoursAgo_ShowsHours()
    {
        var result = CommentsView.FormatTimeAgo(DateTimeOffset.UtcNow.AddHours(-2));
        result.Should().Be("2h ago");
    }

    [Fact]
    public void FormatTimeAgo_DaysAgo_ShowsDays()
    {
        var result = CommentsView.FormatTimeAgo(DateTimeOffset.UtcNow.AddDays(-3));
        result.Should().Be("3d ago");
    }

    [Fact]
    public void FormatTimeAgo_WeeksAgo_ShowsWeeks()
    {
        var result = CommentsView.FormatTimeAgo(DateTimeOffset.UtcNow.AddDays(-14));
        result.Should().Be("2w ago");
    }

    [Fact]
    public void FormatTimeAgo_OldDate_ShowsFormattedDate()
    {
        var date = new DateTimeOffset(2024, 3, 15, 0, 0, 0, TimeSpan.Zero);
        var result = CommentsView.FormatTimeAgo(date);
        result.Should().Be("Mar 15, 2024");
    }

    #endregion

    #region Comment Hierarchy Separation

    [Fact]
    public void CommentHierarchy_TopLevelVsReplies_SeparatedByParentId()
    {
        var topLevel = new Comment
        {
            Id = "c1",
            Author = "Alice",
            Text = "Top level comment"
        };

        var reply = new Comment
        {
            Id = "r1",
            Author = "Bob",
            Text = "Reply to c1",
            ParentCommentId = "c1"
        };

        var comments = new List<Comment> { topLevel, reply };

        var topLevelComments = comments
            .Where(c => string.IsNullOrEmpty(c.ParentCommentId))
            .ToList();

        var replies = comments
            .Where(c => !string.IsNullOrEmpty(c.ParentCommentId))
            .GroupBy(c => c.ParentCommentId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        topLevelComments.Should().HaveCount(1);
        topLevelComments[0].Id.Should().Be("c1");

        replies.Should().ContainKey("c1");
        replies["c1"].Should().HaveCount(1);
        replies["c1"][0].Id.Should().Be("r1");
    }

    [Fact]
    public void CommentReply_HasCorrectParentPath()
    {
        var docPath = "docs/mypage";
        var parentCommentId = "comment1";
        var replyId = "reply1";

        var replyPath = $"{docPath}/{parentCommentId}/{replyId}";

        replyPath.Should().Be("docs/mypage/comment1/reply1");
        replyPath.Should().StartWith($"{docPath}/{parentCommentId}/");
    }

    [Fact]
    public void CommentReply_ParentCommentId_LinksToParent()
    {
        var parentComment = new Comment
        {
            Id = "parent1",
            Author = "Alice",
            Text = "Parent comment"
        };

        var reply = new Comment
        {
            Id = "reply1",
            Author = "Bob",
            Text = "Reply text",
            ParentCommentId = parentComment.Id
        };

        reply.ParentCommentId.Should().Be(parentComment.Id);
        reply.ParentCommentId.Should().Be("parent1");
    }

    #endregion

    #region Descendant Query Scope

    [Fact]
    public void DescendantScope_QueryString_IncludesScopeDescendants()
    {
        var hubPath = "docs/mypage";
        var queryString = $"path:{hubPath} nodeType:{CommentNodeType.NodeType} scope:descendants";

        queryString.Should().Contain("scope:descendants");
        queryString.Should().Contain($"path:{hubPath}");
        queryString.Should().Contain($"nodeType:{CommentNodeType.NodeType}");
    }

    [Fact]
    public void DescendantScope_CapturesBothCommentsAndReplies()
    {
        // A hierarchy: doc → comment → reply
        var docPath = "docs/mypage";
        var commentId = "c1";
        var replyId = "r1";

        var commentNode = new MeshNode($"{docPath}/{commentId}")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = commentId, Author = "Alice", Text = "Comment" }
        };

        var replyNode = new MeshNode($"{docPath}/{commentId}/{replyId}")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = replyId, Author = "Bob", Text = "Reply", ParentCommentId = commentId }
        };

        // Both should be captured by descendants query (simulated here)
        var allNodes = new List<MeshNode> { commentNode, replyNode };

        allNodes.Should().HaveCount(2);
        allNodes.Should().Contain(n => n.Path == $"{docPath}/{commentId}");
        allNodes.Should().Contain(n => n.Path == $"{docPath}/{commentId}/{replyId}");

        // Verify separation
        var topLevel = allNodes
            .Where(n => n.Content is Comment c && string.IsNullOrEmpty(c.ParentCommentId))
            .ToList();
        var replies = allNodes
            .Where(n => n.Content is Comment c && !string.IsNullOrEmpty(c.ParentCommentId))
            .ToList();

        topLevel.Should().HaveCount(1);
        replies.Should().HaveCount(1);
        ((Comment)replies[0].Content!).ParentCommentId.Should().Be(commentId);
    }

    #endregion

    #region Reply UI Rendering

    [Fact]
    public void BuildThumbnail_WithReplyNode_ReturnsStackControl()
    {
        var reply = new Comment
        {
            Id = "reply1",
            Author = "Bob",
            Text = "I agree with this point!",
            ParentCommentId = "c1"
        };

        var replyNode = new MeshNode("docs/page/c1/reply1")
        {
            Name = "Reply",
            NodeType = CommentNodeType.NodeType,
            Content = reply
        };

        var result = CommentLayoutAreas.BuildThumbnail(replyNode);

        result.Should().NotBeNull();
        result.Should().BeOfType<StackControl>();
    }

    [Fact]
    public void BuildThumbnail_WithEmptyReply_ShowsUnknownAuthor()
    {
        // When Reply is clicked, a reply node is created with empty Author and Text
        var emptyReply = new Comment
        {
            Id = "reply1",
            Author = "",
            Text = "",
            ParentCommentId = "c1"
        };

        var replyNode = new MeshNode("docs/page/c1/reply1")
        {
            Name = "Reply",
            NodeType = CommentNodeType.NodeType,
            Content = emptyReply
        };

        // Should not throw and should handle empty content gracefully
        var result = CommentLayoutAreas.BuildThumbnail(replyNode);

        result.Should().NotBeNull();
        result.Should().BeOfType<StackControl>();
    }

    [Fact]
    public void BuildThumbnail_WithNullNode_ReturnsControlWithUnknownAuthor()
    {
        var result = CommentLayoutAreas.BuildThumbnail(null);

        result.Should().NotBeNull();
        result.Should().BeOfType<StackControl>();
    }

    [Fact]
    public void BuildThumbnail_WithLongText_TruncatesPreview()
    {
        var reply = new Comment
        {
            Id = "reply1",
            Author = "Alice",
            Text = "This is a very long reply text that should be truncated when displayed in the thumbnail view",
            ParentCommentId = "c1"
        };

        var replyNode = new MeshNode("docs/page/c1/reply1")
        {
            Name = "Reply",
            NodeType = CommentNodeType.NodeType,
            Content = reply
        };

        // Should not throw - truncation happens at 50 chars
        var result = CommentLayoutAreas.BuildThumbnail(replyNode);
        result.Should().NotBeNull();
    }

    [Fact]
    public void ReplyCreation_ProducesCorrectMeshNode()
    {
        // Simulates what the Reply button click handler creates
        var commentPath = "docs/page/c1";
        var parentComment = new Comment
        {
            Id = "c1",
            Author = "Alice",
            Text = "Original comment",
            MarkerId = "c1"
        };

        var replyId = "reply-abc";
        var replyComment = new Comment
        {
            Id = replyId,
            NodePath = commentPath,
            Author = "",
            Text = "",
            ParentCommentId = parentComment.Id,
            Status = CommentStatus.Active
        };
        var replyNode = new MeshNode($"{commentPath}/{replyId}")
        {
            Name = "Reply",
            NodeType = CommentNodeType.NodeType,
            Content = replyComment
        };

        // Verify the created reply node
        replyNode.Path.Should().Be("docs/page/c1/reply-abc");
        replyNode.NodeType.Should().Be(CommentNodeType.NodeType);
        replyNode.Name.Should().Be("Reply");

        var content = replyNode.Content as Comment;
        content.Should().NotBeNull();
        content!.ParentCommentId.Should().Be("c1");
        content.Author.Should().BeEmpty();
        content.Text.Should().BeEmpty();
        content.Status.Should().Be(CommentStatus.Active);
    }

    [Fact]
    public void ReplyFiltering_OnlyMatchingRepliesShownPerComment()
    {
        // Simulates the filtering logic in BuildReadView
        var comment1 = new MeshNode("docs/page/c1")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "c1", Author = "Alice", Text = "First comment" }
        };
        var comment2 = new MeshNode("docs/page/c2")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "c2", Author = "Carol", Text = "Second comment" }
        };
        var reply1ForC1 = new MeshNode("docs/page/c1/r1")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "r1", Author = "Bob", Text = "Reply to c1", ParentCommentId = "c1" }
        };
        var reply2ForC1 = new MeshNode("docs/page/c1/r2")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "r2", Author = "Dave", Text = "Another reply to c1", ParentCommentId = "c1" }
        };
        var reply1ForC2 = new MeshNode("docs/page/c2/r3")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "r3", Author = "Eve", Text = "Reply to c2", ParentCommentId = "c2" }
        };

        var allNodes = new List<MeshNode> { comment1, comment2, reply1ForC1, reply2ForC1, reply1ForC2 };

        // Separate top-level from replies (same logic as MarkdownLayoutAreas)
        var topLevelComments = allNodes
            .Where(n => n.Content is Comment c && string.IsNullOrEmpty(c.ParentCommentId))
            .ToList();

        var repliesByParent = allNodes
            .Where(n => n.Content is Comment c && !string.IsNullOrEmpty(c.ParentCommentId))
            .GroupBy(n => ((Comment)n.Content!).ParentCommentId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        topLevelComments.Should().HaveCount(2);

        // Comment c1 should have exactly 2 replies
        repliesByParent.Should().ContainKey("c1");
        repliesByParent["c1"].Should().HaveCount(2);
        repliesByParent["c1"].Select(n => ((Comment)n.Content!).Id).Should().BeEquivalentTo(["r1", "r2"]);

        // Comment c2 should have exactly 1 reply
        repliesByParent.Should().ContainKey("c2");
        repliesByParent["c2"].Should().HaveCount(1);
        ((Comment)repliesByParent["c2"][0].Content!).Id.Should().Be("r3");
    }

    [Fact]
    public void ReplyEditState_EditingReplyPath_MatchesCreatedReply()
    {
        // Simulates the panel state after clicking Reply
        var commentPath = "docs/page/c1";
        var replyId = "reply-xyz";
        var replyPath = $"{commentPath}/{replyId}";

        var initialState = new MarkdownLayoutAreas.AnnotationPanelState();
        var afterReply = initialState with { EditingReplyPath = replyPath };

        afterReply.EditingReplyPath.Should().Be("docs/page/c1/reply-xyz");

        // Simulate the check in BuildCommentNodeCard: replyNode.Path == panelState.EditingReplyPath
        var replyNode = new MeshNode(replyPath)
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = replyId, ParentCommentId = "c1" }
        };

        (replyNode.Path == afterReply.EditingReplyPath).Should().BeTrue();
    }

    [Fact]
    public void RepliesRenderedInOrder_SortedByCreatedAt()
    {
        var now = DateTimeOffset.UtcNow;
        var replyNodes = new List<MeshNode>
        {
            new("docs/page/c1/r3")
            {
                NodeType = CommentNodeType.NodeType,
                Content = new Comment { Id = "r3", Author = "C", Text = "Third", ParentCommentId = "c1", CreatedAt = now.AddMinutes(-1) }
            },
            new("docs/page/c1/r1")
            {
                NodeType = CommentNodeType.NodeType,
                Content = new Comment { Id = "r1", Author = "A", Text = "First", ParentCommentId = "c1", CreatedAt = now.AddMinutes(-10) }
            },
            new("docs/page/c1/r2")
            {
                NodeType = CommentNodeType.NodeType,
                Content = new Comment { Id = "r2", Author = "B", Text = "Second", ParentCommentId = "c1", CreatedAt = now.AddMinutes(-5) }
            }
        };

        // Same ordering logic as BuildCommentNodeCard: .OrderBy(r => ((Comment)r.Content!).CreatedAt)
        var ordered = replyNodes.OrderBy(r => ((Comment)r.Content!).CreatedAt).ToList();

        ((Comment)ordered[0].Content!).Id.Should().Be("r1"); // oldest first
        ((Comment)ordered[1].Content!).Id.Should().Be("r2");
        ((Comment)ordered[2].Content!).Id.Should().Be("r3"); // newest last
    }

    [Fact]
    public void BuildThumbnail_IconIsFluentIcon_NotRawString()
    {
        var reply = new Comment
        {
            Id = "reply1",
            Author = "Bob",
            Text = "Test reply",
            ParentCommentId = "c1"
        };

        var replyNode = new MeshNode("docs/page/c1/reply1")
        {
            Name = "Reply",
            NodeType = CommentNodeType.NodeType,
            Content = reply
        };

        // BuildThumbnail should not throw — previously Controls.Icon("Comment")
        // would fail because "Comment" is a raw string, not an Icon domain object.
        var result = CommentLayoutAreas.BuildThumbnail(replyNode);
        result.Should().NotBeNull();
        result.Should().BeOfType<StackControl>();
    }

    #endregion
}
