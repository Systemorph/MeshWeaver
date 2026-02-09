using System;
using System.Collections.Immutable;
using FluentAssertions;
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
}
