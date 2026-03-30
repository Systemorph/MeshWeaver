using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
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
            PrimaryNodePath = "docs/mypage"
        };

        comment.MarkerId.Should().Be("c1");
        comment.HighlightedText.Should().Be("world");
        comment.Author.Should().Be("Alice");
        comment.Text.Should().Be("Great point!");
        comment.PrimaryNodePath.Should().Be("docs/mypage");
    }

    [Fact]
    public void Comment_WithReplies_SupportsThreadingViaMeshNodes()
    {
        var parentComment = new Comment
        {
            Id = "comment-1",
            MarkerId = "c1",
            Author = "Alice",
            Text = "Original comment"
        };

        var reply = new Comment
        {
            Id = "reply-1",
            Author = "Bob",
            Text = "I agree!",
            PrimaryNodePath = "docs/page",
        };

        var parentNode = new MeshNode("docs/page/comment-1")
        {
            NodeType = CommentNodeType.NodeType,
            Content = parentComment
        };

        var replyNode = new MeshNode("docs/page/comment-1/reply-1")
        {
            NodeType = CommentNodeType.NodeType,
            Content = reply
        };

        // Threading is via MeshNode path hierarchy
        ((Comment)replyNode.Content!).Author.Should().Be("Bob");
        replyNode.Path.Should().StartWith(parentNode.Path!);
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

    [Fact]
    public void Comment_PrimaryNodePath_PointsToDocument_ForReplies()
    {
        // Top-level comment: PrimaryNodePath is the document path
        var topLevel = new Comment
        {
            Id = "c1",
            PrimaryNodePath = "docs/page",
            Author = "Alice",
            Text = "Top-level comment"
        };

        topLevel.PrimaryNodePath.Should().Be("docs/page");

        // Reply: PrimaryNodePath is still the original document, not the parent comment
        var reply = new Comment
        {
            Id = "r1",
            PrimaryNodePath = "docs/page",
            Author = "Bob",
            Text = "Reply"
        };

        reply.PrimaryNodePath.Should().Be("docs/page");
    }

    [Fact]
    public void Comment_PrimaryNodePath_DefaultsToEmpty()
    {
        var comment = new Comment();
        comment.PrimaryNodePath.Should().BeEmpty();
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
            PrimaryNodePath = "docs/page",
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
            PrimaryNodePath = "MeshWeaver/Documentation/DataMesh/CollaborativeEditing",
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

    // FormatTimeAgo is defined on CollaborativeMarkdownView (Blazor project).
    // These tests use an inline copy for isolation.
    private static string FormatTimeAgo(DateTimeOffset dateTime)
    {
        var timeSpan = DateTimeOffset.UtcNow - dateTime;
        if (timeSpan.TotalMinutes < 1) return "just now";
        if (timeSpan.TotalMinutes < 60) return $"{(int)timeSpan.TotalMinutes}m ago";
        if (timeSpan.TotalHours < 24) return $"{(int)timeSpan.TotalHours}h ago";
        if (timeSpan.TotalDays < 7) return $"{(int)timeSpan.TotalDays}d ago";
        if (timeSpan.TotalDays < 30) return $"{(int)(timeSpan.TotalDays / 7)}w ago";
        return dateTime.ToString("MMM d, yyyy");
    }

    [Fact]
    public void FormatTimeAgo_RecentDate_ShowsJustNow()
    {
        var result = FormatTimeAgo(DateTimeOffset.UtcNow);
        result.Should().Be("just now");
    }

    [Fact]
    public void FormatTimeAgo_MinutesAgo_ShowsMinutes()
    {
        var result = FormatTimeAgo(DateTimeOffset.UtcNow.AddMinutes(-5));
        result.Should().Be("5m ago");
    }

    [Fact]
    public void FormatTimeAgo_HoursAgo_ShowsHours()
    {
        var result = FormatTimeAgo(DateTimeOffset.UtcNow.AddHours(-2));
        result.Should().Be("2h ago");
    }

    [Fact]
    public void FormatTimeAgo_DaysAgo_ShowsDays()
    {
        var result = FormatTimeAgo(DateTimeOffset.UtcNow.AddDays(-3));
        result.Should().Be("3d ago");
    }

    [Fact]
    public void FormatTimeAgo_WeeksAgo_ShowsWeeks()
    {
        var result = FormatTimeAgo(DateTimeOffset.UtcNow.AddDays(-14));
        result.Should().Be("2w ago");
    }

    [Fact]
    public void FormatTimeAgo_OldDate_ShowsFormattedDate()
    {
        var date = new DateTimeOffset(2024, 3, 15, 0, 0, 0, TimeSpan.Zero);
        var result = FormatTimeAgo(date);
        result.Should().Be("Mar 15, 2024");
    }

    #endregion

    #region Comment Hierarchy Separation

    [Fact]
    public void CommentHierarchy_TopLevelVsReplies_SeparatedByPathDepth()
    {
        var docPath = "docs/page";
        var topLevelNode = new MeshNode($"{docPath}/c1")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "c1", Author = "Alice", Text = "Top level comment" }
        };

        var replyNode = new MeshNode($"{docPath}/c1/r1")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "r1", Author = "Bob", Text = "Reply to c1" }
        };

        var allNodes = new List<MeshNode> { topLevelNode, replyNode };
        var docSegments = docPath.Split('/').Length;

        // Top-level comments are direct children of doc (depth = docSegments + 1)
        var topLevelComments = allNodes
            .Where(n => n.Segments.Count == docSegments + 1)
            .ToList();

        // Replies are deeper (depth > docSegments + 1)
        var replies = allNodes
            .Where(n => n.Segments.Count > docSegments + 1)
            .ToList();

        topLevelComments.Should().HaveCount(1);
        ((Comment)topLevelComments[0].Content!).Id.Should().Be("c1");

        replies.Should().HaveCount(1);
        ((Comment)replies[0].Content!).Id.Should().Be("r1");
        replies[0].Path.Should().StartWith(topLevelNode.Path!);
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
    public void CommentReply_PathHierarchy_LinksToParent()
    {
        var parentNode = new MeshNode("docs/page/parent1")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "parent1", Author = "Alice", Text = "Parent comment" }
        };

        var replyNode = new MeshNode("docs/page/parent1/reply1")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "reply1", Author = "Bob", Text = "Reply text" }
        };

        replyNode.Path.Should().StartWith(parentNode.Path!);
        replyNode.Path.Should().Be("docs/page/parent1/reply1");
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
            Content = new Comment { Id = replyId, Author = "Bob", Text = "Reply" }
        };

        // Both should be captured by descendants query (simulated here)
        var allNodes = new List<MeshNode> { commentNode, replyNode };

        allNodes.Should().HaveCount(2);
        allNodes.Should().Contain(n => n.Path == $"{docPath}/{commentId}");
        allNodes.Should().Contain(n => n.Path == $"{docPath}/{commentId}/{replyId}");

        // Verify separation via path hierarchy
        var docSegments = docPath.Split('/').Length;
        var topLevel = allNodes
            .Where(n => n.Segments.Count == docSegments + 1)
            .ToList();
        var replies = allNodes
            .Where(n => n.Segments.Count > docSegments + 1)
            .ToList();

        topLevel.Should().HaveCount(1);
        replies.Should().HaveCount(1);
        replies[0].Path.Should().StartWith($"{docPath}/{commentId}/");
    }

    #endregion

    #region Reply Path Structure

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
            PrimaryNodePath = "docs/page",
            Author = "",
            Text = "",
            Status = CommentStatus.Active
        };
        var replyNode = new MeshNode($"{commentPath}/{replyId}")
        {
            Name = "Reply",
            NodeType = CommentNodeType.NodeType,
            Content = replyComment
        };

        // Verify the created reply node — parent relationship is encoded in path
        replyNode.Path.Should().Be("docs/page/c1/reply-abc");
        replyNode.Path.Should().StartWith(commentPath);
        replyNode.NodeType.Should().Be(CommentNodeType.NodeType);
        replyNode.Name.Should().Be("Reply");

        var content = replyNode.Content as Comment;
        content.Should().NotBeNull();
        content!.Author.Should().BeEmpty();
        content.Text.Should().BeEmpty();
        content.Status.Should().Be(CommentStatus.Active);
        content.PrimaryNodePath.Should().Be("docs/page");
    }

    [Fact]
    public void ReplyFiltering_OnlyMatchingRepliesShownPerComment()
    {
        // Simulates the filtering logic in BuildReadView using MeshNode path hierarchy
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
            Content = new Comment { Id = "r1", Author = "Bob", Text = "Reply to c1" }
        };
        var reply2ForC1 = new MeshNode("docs/page/c1/r2")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "r2", Author = "Dave", Text = "Another reply to c1" }
        };
        var reply1ForC2 = new MeshNode("docs/page/c2/r3")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "r3", Author = "Eve", Text = "Reply to c2" }
        };

        var allNodes = new List<MeshNode> { comment1, comment2, reply1ForC1, reply2ForC1, reply1ForC2 };
        var docSegments = "docs/page".Split('/').Length;

        // Separate top-level from replies via path depth
        var topLevelComments = allNodes
            .Where(n => n.Segments.Count == docSegments + 1)
            .ToList();

        // Group replies by parent path
        var repliesByParent = allNodes
            .Where(n => n.Segments.Count > docSegments + 1)
            .GroupBy(n => string.Join("/", n.Segments.Take(n.Segments.Count - 1)))
            .ToDictionary(g => g.Key, g => g.ToList());

        topLevelComments.Should().HaveCount(2);

        // Comment c1 should have exactly 2 replies
        repliesByParent.Should().ContainKey("docs/page/c1");
        repliesByParent["docs/page/c1"].Should().HaveCount(2);
        repliesByParent["docs/page/c1"].Select(n => ((Comment)n.Content!).Id).Should().BeEquivalentTo(["r1", "r2"]);

        // Comment c2 should have exactly 1 reply
        repliesByParent.Should().ContainKey("docs/page/c2");
        repliesByParent["docs/page/c2"].Should().HaveCount(1);
        ((Comment)repliesByParent["docs/page/c2"][0].Content!).Id.Should().Be("r3");
    }

    [Fact]
    public void ReplyEditState_EditingReplyPath_MatchesCreatedReply()
    {
        // Simulates a reply being edited inline — the path matches the created reply node
        var commentPath = "docs/page/c1";
        var replyId = "reply-xyz";
        var replyPath = $"{commentPath}/{replyId}";

        var replyNode = new MeshNode(replyPath)
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = replyId }
        };

        replyNode.Path.Should().Be("docs/page/c1/reply-xyz");
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
                Content = new Comment { Id = "r3", Author = "C", Text = "Third", CreatedAt = now.AddMinutes(-1) }
            },
            new("docs/page/c1/r1")
            {
                NodeType = CommentNodeType.NodeType,
                Content = new Comment { Id = "r1", Author = "A", Text = "First", CreatedAt = now.AddMinutes(-10) }
            },
            new("docs/page/c1/r2")
            {
                NodeType = CommentNodeType.NodeType,
                Content = new Comment { Id = "r2", Author = "B", Text = "Second", CreatedAt = now.AddMinutes(-5) }
            }
        };

        // Same ordering logic as BuildCommentNodeCard: .OrderBy(r => ((Comment)r.Content!).CreatedAt)
        var ordered = replyNodes.OrderBy(r => ((Comment)r.Content!).CreatedAt).ToList();

        ((Comment)ordered[0].Content!).Id.Should().Be("r1"); // oldest first
        ((Comment)ordered[1].Content!).Id.Should().Be("r2");
        ((Comment)ordered[2].Content!).Id.Should().Be("r3"); // newest last
    }

    #endregion

    #region Reply System — MeshNode-Based

    [Fact]
    public void Comment_Record_HasRepliesProperty_AsImmutableListOfPaths()
    {
        var repliesProp = typeof(Comment).GetProperty("Replies");
        repliesProp.Should().NotBeNull("Comment.Replies stores paths to reply MeshNodes");
        repliesProp!.PropertyType.Should().Be(typeof(ImmutableList<string>),
            "Replies should be ImmutableList<string> (paths to reply nodes), not full Comment objects");

        // Verify default value is empty
        var comment = new Comment();
        comment.Replies.Should().BeEmpty("new comments should have no replies by default");
    }

    [Fact]
    public void ReplyCreation_SetsPathHierarchy_And_NodeType()
    {
        var commentPath = "docs/page/c1";
        var replyId = "reply-new";

        var replyComment = new Comment
        {
            Id = replyId,
            PrimaryNodePath = "docs/page",
            Author = "",
            Text = "",
            Status = CommentStatus.Active
        };
        var replyNode = new MeshNode($"{commentPath}/{replyId}")
        {
            Name = "Reply",
            NodeType = CommentNodeType.NodeType,
            Content = replyComment
        };

        replyNode.NodeType.Should().Be(CommentNodeType.NodeType);
        replyNode.Path.Should().Be("docs/page/c1/reply-new");
        replyNode.Path.Should().StartWith(commentPath);
    }

    [Fact]
    public void ReplyNodes_SortedByCreatedAt()
    {
        var now = DateTimeOffset.UtcNow;
        var replyNodes = new List<MeshNode>
        {
            new("docs/page/c1/r3")
            {
                NodeType = CommentNodeType.NodeType,
                Content = new Comment { Id = "r3", Author = "C", Text = "Third", CreatedAt = now.AddMinutes(-1) }
            },
            new("docs/page/c1/r1")
            {
                NodeType = CommentNodeType.NodeType,
                Content = new Comment { Id = "r1", Author = "A", Text = "First", CreatedAt = now.AddMinutes(-10) }
            },
            new("docs/page/c1/r2")
            {
                NodeType = CommentNodeType.NodeType,
                Content = new Comment { Id = "r2", Author = "B", Text = "Second", CreatedAt = now.AddMinutes(-5) }
            }
        };

        var ordered = replyNodes.OrderBy(r => ((Comment)r.Content!).CreatedAt).ToList();

        ((Comment)ordered[0].Content!).Id.Should().Be("r1");
        ((Comment)ordered[1].Content!).Id.Should().Be("r2");
        ((Comment)ordered[2].Content!).Id.Should().Be("r3");
    }

    #endregion

    #region Comment GUI Workflow — Selection to Comment to Reply

    [Fact]
    public void CommentWorkflow_SelectText_InsertMarker_CreateComment()
    {
        // Simulates the full GUI flow: user selects text → clicks Comment → marker inserted → Comment entity created
        var rawMarkdown = "MeshWeaver is a powerful platform for building collaborative applications.";
        var selectedText = "powerful platform";
        var markerId = "abc123";
        var author = "Alice";

        // Step 1: Insert marker around selected text (done by CollaborativeMarkdownView.OnCommentFromSelection)
        var idx = rawMarkdown.IndexOf(selectedText, StringComparison.Ordinal);
        idx.Should().BeGreaterThanOrEqualTo(0);
        var openTag = $"<!--comment:{markerId}:{author}:Mar 14-->";
        var closeTag = $"<!--/comment:{markerId}-->";
        var annotatedMarkdown = rawMarkdown.Insert(idx + selectedText.Length, closeTag).Insert(idx, openTag);

        annotatedMarkdown.Should().Contain($"{openTag}{selectedText}{closeTag}");

        // Step 2: Parse annotations from the updated markdown
        var annotations = AnnotationParser.ExtractAnnotations(annotatedMarkdown);
        annotations.Should().HaveCount(1);
        annotations[0].Id.Should().Be(markerId);
        annotations[0].HighlightedText.Should().Be(selectedText);
        annotations[0].Author.Should().Be(author);
        annotations[0].Type.Should().Be(MarkdownAnnotationType.Comment);

        // Step 3: Create Comment entity linked to the marker
        var docPath = "Doc/DataMesh/MyPage";
        var commentPartition = "_Comment";
        var comment = new Comment
        {
            Id = markerId,
            PrimaryNodePath = docPath,
            MarkerId = markerId,
            HighlightedText = selectedText,
            Author = author,
            Text = "",  // Empty initially — user types text after creation
            Status = CommentStatus.Active
        };
        var commentNode = new MeshNode(markerId, $"{docPath}/{commentPartition}")
        {
            Name = $"Comment by {author}",
            NodeType = CommentNodeType.NodeType,
            Content = comment
        };

        commentNode.Path.Should().Be($"{docPath}/{commentPartition}/{markerId}");
        commentNode.NodeType.Should().Be("Comment");
        ((Comment)commentNode.Content!).MarkerId.Should().Be(markerId);
        ((Comment)commentNode.Content!).HighlightedText.Should().Be(selectedText);
    }

    [Fact]
    public void CommentWorkflow_ReplyCreatedUnderParentComment_NotDocument()
    {
        // The Reply button creates a reply under the PARENT COMMENT, not the document.
        // This matches BuildReplyButton: new MeshNode(replyId, hubPath)
        // where hubPath = parent comment's address
        var docPath = "Doc/DataMesh/CollaborativeEditing";
        var commentPartition = "_Comment";
        var parentCommentPath = $"{docPath}/{commentPartition}/c1";

        var replyId = "reply-new";
        var replyNode = new MeshNode(replyId, parentCommentPath)
        {
            Name = "Reply to Alice",
            NodeType = CommentNodeType.NodeType,
            Content = new Comment
            {
                Id = replyId,
                PrimaryNodePath = docPath,  // Points to DOCUMENT, not parent comment
                Author = "Bob",
                Text = "",
                Status = CommentStatus.Active
            }
        };

        // Reply path should be under the parent comment
        replyNode.Path.Should().Be($"{parentCommentPath}/{replyId}");
        replyNode.Path.Should().StartWith(parentCommentPath);

        // Reply namespace should be the parent comment path
        replyNode.Namespace.Should().Be(parentCommentPath);

        // PrimaryNodePath should always point to the original document
        ((Comment)replyNode.Content!).PrimaryNodePath.Should().Be(docPath);
    }

    [Fact]
    public void CommentWorkflow_ReplyDiscoverableViaNamespaceQuery()
    {
        // Simulates what CommentLayoutAreas.Overview does to find replies:
        // meshQuery.ObserveQuery("namespace:{hubPath} nodeType:Comment")
        var docPath = "Doc/DataMesh/CollaborativeEditing";
        var commentPartition = "_Comment";
        var parentCommentPath = $"{docPath}/{commentPartition}/c1";

        // Parent comment
        var parentNode = new MeshNode("c1", $"{docPath}/{commentPartition}")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "c1", Author = "Alice", Text = "Comment" }
        };

        // Reply under parent
        var replyNode = new MeshNode("reply1", parentCommentPath)
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "reply1", Author = "Bob", Text = "Reply" }
        };

        // Simulate namespace query filtering
        var allNodes = new List<MeshNode> { parentNode, replyNode };

        // namespace:{parentCommentPath} should match nodes whose Namespace == parentCommentPath
        var repliesQuery = allNodes.Where(n => n.Namespace == parentCommentPath).ToList();

        repliesQuery.Should().HaveCount(1, "Only reply1 has namespace == parentCommentPath");
        repliesQuery[0].Id.Should().Be("reply1");

        // Parent comment has namespace = docPath/_Comment, not parentCommentPath
        parentNode.Namespace.Should().Be($"{docPath}/{commentPartition}");
        parentNode.Namespace.Should().NotBe(parentCommentPath);
    }

    [Fact]
    public void CommentWorkflow_ResolveComment_RemovesMarkerKeepsText()
    {
        // Full resolve flow: comment is resolved → marker removed from markdown → text kept
        var markdown = "Hello <!--comment:c1-->world<!--/comment:c1--> today!";

        // Step 1: Resolve removes the marker but keeps the text
        var resolved = AnnotationMarkdownExtension.ResolveComment(markdown, "c1");
        resolved.Should().Be("Hello world today!");

        // Step 2: No more annotations after resolve
        var remaining = AnnotationParser.ExtractAnnotations(resolved);
        remaining.Should().BeEmpty();
    }

    [Fact]
    public void CommentWorkflow_MultipleComments_IndependentResolve()
    {
        var markdown = "<!--comment:c1-->first<!--/comment:c1--> and <!--comment:c2-->second<!--/comment:c2-->";

        // Resolve c1 only
        var afterC1 = AnnotationMarkdownExtension.ResolveComment(markdown, "c1");
        afterC1.Should().Contain("first");
        afterC1.Should().NotContain("<!--comment:c1-->");
        afterC1.Should().Contain("<!--comment:c2-->second<!--/comment:c2-->");

        // Resolve c2 from the result
        var afterBoth = AnnotationMarkdownExtension.ResolveComment(afterC1, "c2");
        afterBoth.Should().Be("first and second");
    }

    [Fact]
    public void CommentWorkflow_SidebarRendersCommentWithMetadata()
    {
        // Simulates what CollaborativeMarkdownView does:
        // Parse annotations from markdown → render sidebar cards with comment data
        var markdown = "<!--comment:c1:Alice:Dec 15-->highlighted text<!--/comment:c1-->";

        var annotations = AnnotationParser.ExtractAnnotations(markdown);
        annotations.Should().HaveCount(1);

        var ann = annotations[0];
        ann.Id.Should().Be("c1");
        ann.Type.Should().Be(MarkdownAnnotationType.Comment);
        ann.Author.Should().Be("Alice");
        ann.Date.Should().Be("Dec 15");
        ann.HighlightedText.Should().Be("highlighted text");

        // Sidebar card would show:
        // - Author: Alice
        // - Time: Dec 15
        // - Highlighted text quote: "highlighted text"
        // - Comment text from Comment entity (loaded separately via mesh query)
    }

    [Fact]
    public void CommentWorkflow_CommentsPartition_ContainsOnlyTopLevelComments()
    {
        // CommentsView.Comments queries namespace:{docPath}/_Comment
        // This should find only top-level comments (c1, c2, etc.)
        // NOT replies (which are in namespace:{docPath}/_Comment/c1)
        var docPath = "Doc/DataMesh/CollaborativeEditing";
        var commentPartition = "_Comment";
        var commentNs = $"{docPath}/{commentPartition}";

        var c1 = new MeshNode("c1", commentNs)
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "c1", Author = "Alice" }
        };
        var c2 = new MeshNode("c2", commentNs)
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "c2", Author = "Bob" }
        };
        var reply1 = new MeshNode("reply1", $"{commentNs}/c1")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Id = "reply1", Author = "Carol" }
        };

        var allNodes = new List<MeshNode> { c1, c2, reply1 };

        // CommentsView query: namespace:{docPath}/_Comment
        var topLevelComments = allNodes.Where(n => n.Namespace == commentNs).ToList();
        topLevelComments.Should().HaveCount(2);
        topLevelComments.Select(n => n.Id).Should().BeEquivalentTo(["c1", "c2"]);

        // reply1 should NOT appear in top-level query
        topLevelComments.Should().NotContain(n => n.Id == "reply1");

        // Reply1 appears in c1's namespace query
        var c1Replies = allNodes.Where(n => n.Namespace == $"{commentNs}/c1").ToList();
        c1Replies.Should().HaveCount(1);
        c1Replies[0].Id.Should().Be("reply1");
    }

    [Fact]
    public void CommentWorkflow_IsTopLevelComment_DetectsCorrectly()
    {
        // Tests the IsTopLevelComment logic from CommentLayoutAreas
        var docPath = "Doc/DataMesh/CollaborativeEditing";
        var commentPartition = "_Comment";

        // Top-level comment at {docPath}/_Comment/c1
        var topLevelPath = $"{docPath}/{commentPartition}/c1";
        var topLevelComment = new Comment
        {
            Id = "c1",
            PrimaryNodePath = docPath,
            Author = "Alice"
        };

        // Top-level comments should return true (Resolve button shows)
        CommentLayoutAreas.IsTopLevelComment(topLevelPath, topLevelComment).Should().BeTrue(
            "Top-level comment at {docPath}/_Comment/c1 should be detected as top-level");

        // Reply at {docPath}/_Comment/c1/reply1
        var replyPath = $"{docPath}/{commentPartition}/c1/reply1";
        var replyComment = new Comment
        {
            Id = "reply1",
            PrimaryNodePath = docPath,
            Author = "Bob"
        };

        // Replies should return false (no Resolve button)
        CommentLayoutAreas.IsTopLevelComment(replyPath, replyComment).Should().BeFalse(
            "Reply at {docPath}/_Comment/c1/reply1 should NOT be detected as top-level");

        // Comment with empty PrimaryNodePath should return true
        var noPrimary = new Comment { PrimaryNodePath = "" };
        CommentLayoutAreas.IsTopLevelComment("any/path", noPrimary).Should().BeTrue(
            "Comment with empty PrimaryNodePath should default to top-level");

        // Deeper nested reply should also return false
        var deepReplyPath = $"{docPath}/{commentPartition}/c1/reply1/sub1";
        CommentLayoutAreas.IsTopLevelComment(deepReplyPath, replyComment).Should().BeFalse(
            "Deeply nested reply should NOT be detected as top-level");
    }

    #endregion

    #region Collaborative Editing Sample Data

    [Fact]
    public void CollaborativeEditingSample_ReplyStructure_MatchesExpected()
    {
        // Verify the expected structure for the CollaborativeEditing sample data:
        // Doc/DataMesh/CollaborativeEditing/_Comment/c1       → top-level comment by Alice
        // Doc/DataMesh/CollaborativeEditing/_Comment/c1/reply1 → reply to c1
        var docPath = "Doc/DataMesh/CollaborativeEditing";
        var commentPartition = "_Comment";

        var c1Path = $"{docPath}/{commentPartition}/c1";
        var reply1Path = $"{c1Path}/reply1";

        // c1 node
        var c1 = new MeshNode("c1", $"{docPath}/{commentPartition}")
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment
            {
                Id = "c1",
                PrimaryNodePath = docPath,
                MarkerId = "c1",
                HighlightedText = "powerful platform",
                Author = "Alice",
                Text = "Can we add specific metrics here?"
            }
        };

        // reply1 node (child of c1)
        var reply1 = new MeshNode("reply1", c1Path)
        {
            NodeType = CommentNodeType.NodeType,
            Content = new Comment
            {
                Id = "reply1",
                PrimaryNodePath = docPath,
                Author = "Roland",
                Text = "We should add benchmarks from the latest performance tests."
            }
        };

        c1.Path.Should().Be(c1Path);
        c1.Namespace.Should().Be($"{docPath}/{commentPartition}");
        reply1.Path.Should().Be(reply1Path);
        reply1.Namespace.Should().Be(c1Path);
        reply1.Path.Should().StartWith(c1.Path!);

        // PrimaryNodePath should be the same for both (the document)
        ((Comment)c1.Content!).PrimaryNodePath.Should().Be(docPath);
        ((Comment)reply1.Content!).PrimaryNodePath.Should().Be(docPath);
    }

    [Fact]
    public void CollaborativeEditingSample_AllSixComments_HaveMarkers()
    {
        // The sample CollaborativeEditing.md has 6 inline comment markers (c1-c6)
        var markdown = @"> MeshWeaver is a <!--comment:c1-->powerful platform<!--/comment:c1--> for building <!--comment:c2-->collaborative applications<!--/comment:c2-->. It provides real-time synchronization and <!--comment:c3-->conflict-free editing<!--/comment:c3-->.

> Our team has completed the analysis of the <!--comment:c4-->market trends<!--/comment:c4-->.

> The <!--comment:c5-->proposed timeline<!--/comment:c5--> for Phase 1.

> - <!--comment:c6-->Additional resources<!--/comment:c6--> from the engineering team";

        var annotations = AnnotationParser.ExtractAnnotations(markdown);
        var comments = annotations.Where(a => a.Type == MarkdownAnnotationType.Comment).ToList();

        comments.Should().HaveCount(6);
        comments.Select(c => c.Id).Should().BeEquivalentTo(["c1", "c2", "c3", "c4", "c5", "c6"]);
    }

    #endregion

    #region Comment Insertion with Existing Annotations

    /// <summary>
    /// Simulates the OnCommentFromSelection flow: strip markers, find text in clean,
    /// map back to annotated positions, insert new comment markers.
    /// </summary>
    private static string SimulateCommentFromSelection(string rawContent, string selectedText, string markerId)
    {
        var cleanContent = MarkdownAnnotationParser.StripAllMarkers(rawContent);
        var idx = cleanContent.IndexOf(selectedText, StringComparison.Ordinal);
        idx.Should().BeGreaterThanOrEqualTo(0, $"Selected text '{selectedText}' should be found in clean content");

        var map = MarkdownAnnotationParser.BuildCleanToAnnotatedMap(rawContent);
        var aStart = idx < map.Length ? map[idx] : rawContent.Length;
        var aEnd = (idx + selectedText.Length) < map.Length
            ? map[idx + selectedText.Length]
            : rawContent.Length;

        var openTag = $"<!--comment:{markerId}-->";
        var closeTag = $"<!--/comment:{markerId}-->";
        return rawContent.Insert(aEnd, closeTag).Insert(aStart, openTag);
    }

    [Fact]
    public void CommentFromSelection_WithExistingAnnotation_MapsCleanPositionCorrectly()
    {
        var rawContent = "Hello <!--comment:c1-->world<!--/comment:c1--> today";

        var result = SimulateCommentFromSelection(rawContent, "today", "c2");

        // Both annotations should parse correctly
        var annotations = AnnotationParser.ExtractAnnotations(result);
        annotations.Should().HaveCount(2);
        annotations.Should().Contain(a => a.Id == "c1" && a.HighlightedText == "world");
        annotations.Should().Contain(a => a.Id == "c2" && a.HighlightedText == "today");
    }

    [Fact]
    public void CommentFromSelection_WithExistingInsertDelete_InsertsCorrectly()
    {
        var rawContent = "Hello <!--insert:i1:Bob:Dec 16-->beautiful <!--/insert:i1-->" +
                         "<!--delete:d1:Bob:Dec 16-->ugly <!--/delete:d1-->world";

        var result = SimulateCommentFromSelection(rawContent, "world", "c1");

        var annotations = AnnotationParser.ExtractAnnotations(result);
        annotations.Should().HaveCount(3);
        annotations.Should().Contain(a => a.Id == "i1" && a.Type == MarkdownAnnotationType.Insert);
        annotations.Should().Contain(a => a.Id == "d1" && a.Type == MarkdownAnnotationType.Delete);
        annotations.Should().Contain(a => a.Id == "c1" && a.Type == MarkdownAnnotationType.Comment && a.HighlightedText == "world");
    }

    [Fact]
    public void CommentFromSelection_AdjacentToExistingAnnotation_InsertsCorrectly()
    {
        // Text immediately after an annotation's closing tag
        var rawContent = "<!--comment:c1-->first<!--/comment:c1-->second";

        var result = SimulateCommentFromSelection(rawContent, "second", "c2");

        var annotations = AnnotationParser.ExtractAnnotations(result);
        annotations.Should().HaveCount(2);
        annotations.Should().Contain(a => a.Id == "c1" && a.HighlightedText == "first");
        annotations.Should().Contain(a => a.Id == "c2" && a.HighlightedText == "second");
    }

    [Fact]
    public void CommentFromSelection_TextBeforeExistingAnnotation_InsertsCorrectly()
    {
        var rawContent = "Hello world <!--comment:c1-->annotated<!--/comment:c1--> text";

        var result = SimulateCommentFromSelection(rawContent, "Hello", "c2");

        var annotations = AnnotationParser.ExtractAnnotations(result);
        annotations.Should().HaveCount(2);
        annotations.Should().Contain(a => a.Id == "c1" && a.HighlightedText == "annotated");
        annotations.Should().Contain(a => a.Id == "c2" && a.HighlightedText == "Hello");
    }

    [Fact]
    public void CommentFromSelection_MultipleExistingAnnotations_MapsAllPositionsCorrectly()
    {
        var rawContent = "A <!--comment:c1-->B<!--/comment:c1--> C <!--insert:i1-->D<!--/insert:i1--> E";

        var result = SimulateCommentFromSelection(rawContent, "E", "c2");

        var annotations = AnnotationParser.ExtractAnnotations(result);
        annotations.Should().HaveCount(3);
        annotations.Should().Contain(a => a.Id == "c1" && a.HighlightedText == "B");
        annotations.Should().Contain(a => a.Id == "i1" && a.HighlightedText == "D");
        annotations.Should().Contain(a => a.Id == "c2" && a.HighlightedText == "E");

        // Verify the original annotations are untouched
        result.Should().Contain("<!--comment:c1-->B<!--/comment:c1-->");
        result.Should().Contain("<!--insert:i1-->D<!--/insert:i1-->");
    }

    #endregion
}
