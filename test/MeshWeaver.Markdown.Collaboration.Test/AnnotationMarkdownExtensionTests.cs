using FluentAssertions;
using MeshWeaver.Markdown;
using Xunit;
using MarkdownAnnotationType = MeshWeaver.Markdown.AnnotationType;

namespace MeshWeaver.Markdown.Collaboration.Test;

public class AnnotationMarkdownExtensionTests
{
    #region TransformAnnotations Tests

    [Fact]
    public void TransformAnnotations_CommentMarker_ConvertsToSpan()
    {
        var markdown = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Contain("<span class=\"comment-highlight\" data-comment-id=\"c1\">");
        result.Should().Contain(">world</span>");
    }

    [Fact]
    public void TransformAnnotations_InsertMarker_ConvertsToSpan()
    {
        var markdown = "Hello <!--insert:i1-->new text<!--/insert:i1--> world";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Contain("<span class=\"track-insert\" data-change-id=\"i1\">");
        result.Should().Contain(">new text</span>");
    }

    [Fact]
    public void TransformAnnotations_DeleteMarker_ConvertsToSpan()
    {
        var markdown = "Hello <!--delete:d1-->old text<!--/delete:d1--> world";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Contain("<span class=\"track-delete\" data-change-id=\"d1\">");
        result.Should().Contain(">old text</span>");
    }

    [Fact]
    public void TransformAnnotations_MixedMarkers_ConvertsAll()
    {
        var markdown = "<!--comment:c1-->commented<!--/comment:c1--> " +
                       "<!--insert:i1-->inserted<!--/insert:i1--> " +
                       "<!--delete:d1-->deleted<!--/delete:d1-->";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Contain("class=\"comment-highlight\"");
        result.Should().Contain("data-comment-id=\"c1\"");
        result.Should().Contain("class=\"track-insert\"");
        result.Should().Contain("data-change-id=\"i1\"");
        result.Should().Contain("class=\"track-delete\"");
        result.Should().Contain("data-change-id=\"d1\"");
    }

    [Fact]
    public void TransformAnnotations_MultilineContent_Works()
    {
        var markdown = "Start <!--comment:c1-->line1\nline2\nline3<!--/comment:c1--> end";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Contain("class=\"comment-highlight\" data-comment-id=\"c1\"");
        result.Should().Contain("line1\nline2\nline3");
    }

    [Fact]
    public void TransformAnnotations_NoMarkers_ReturnsUnchanged()
    {
        var markdown = "Just plain text without any markers.";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Be(markdown);
    }

    [Fact]
    public void TransformAnnotations_NullInput_ReturnsNull()
    {
        var result = AnnotationMarkdownExtension.TransformAnnotations(null!);

        result.Should().BeNull();
    }

    [Fact]
    public void TransformAnnotations_EmptyInput_ReturnsEmpty()
    {
        var result = AnnotationMarkdownExtension.TransformAnnotations(string.Empty);

        result.Should().BeEmpty();
    }

    #endregion

    #region HasAnnotations Tests

    [Fact]
    public void HasAnnotations_WithComment_ReturnsTrue()
    {
        var markdown = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        AnnotationMarkdownExtension.HasAnnotations(markdown).Should().BeTrue();
    }

    [Fact]
    public void HasAnnotations_WithInsert_ReturnsTrue()
    {
        var markdown = "Hello <!--insert:i1-->text<!--/insert:i1-->!";

        AnnotationMarkdownExtension.HasAnnotations(markdown).Should().BeTrue();
    }

    [Fact]
    public void HasAnnotations_WithDelete_ReturnsTrue()
    {
        var markdown = "Hello <!--delete:d1-->text<!--/delete:d1-->!";

        AnnotationMarkdownExtension.HasAnnotations(markdown).Should().BeTrue();
    }

    [Fact]
    public void HasAnnotations_NoMarkers_ReturnsFalse()
    {
        var markdown = "Just plain text.";

        AnnotationMarkdownExtension.HasAnnotations(markdown).Should().BeFalse();
    }

    #endregion

    #region StripAnnotations Tests

    [Fact]
    public void StripAnnotations_RemovesMarkersKeepsText()
    {
        var markdown = "Hello <!--comment:c1-->beautiful<!--/comment:c1--> " +
                       "<!--insert:i1-->world<!--/insert:i1--> " +
                       "<!--delete:d1-->today<!--/delete:d1-->!";

        var result = AnnotationMarkdownExtension.StripAnnotations(markdown);

        result.Should().Be("Hello beautiful world today!");
    }

    #endregion

    #region GetAcceptedContent Tests

    [Fact]
    public void GetAcceptedContent_KeepsInserts_RemovesDeletes()
    {
        // Accepting means:
        // - Insertions are kept (they were added)
        // - Deletions are removed (they were deleted)
        var markdown = "Hello <!--insert:i1-->beautiful <!--/insert:i1-->" +
                       "<!--delete:d1-->ugly <!--/delete:d1-->world!";

        var result = AnnotationMarkdownExtension.GetAcceptedContent(markdown);

        result.Should().Be("Hello beautiful world!");
    }

    [Fact]
    public void GetAcceptedContent_RemovesCommentMarkers()
    {
        var markdown = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        var result = AnnotationMarkdownExtension.GetAcceptedContent(markdown);

        result.Should().Be("Hello world!");
    }

    #endregion

    #region GetRejectedContent Tests

    [Fact]
    public void GetRejectedContent_RemovesInserts_KeepsDeletes()
    {
        // Rejecting means:
        // - Insertions are removed (they should not have been added)
        // - Deletions are kept (the text should not have been deleted)
        var markdown = "Hello <!--insert:i1-->ugly <!--/insert:i1-->" +
                       "<!--delete:d1-->beautiful <!--/delete:d1-->world!";

        var result = AnnotationMarkdownExtension.GetRejectedContent(markdown);

        result.Should().Be("Hello beautiful world!");
    }

    [Fact]
    public void GetRejectedContent_RemovesCommentMarkers()
    {
        var markdown = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        var result = AnnotationMarkdownExtension.GetRejectedContent(markdown);

        result.Should().Be("Hello world!");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void TransformAnnotations_SpecialCharsInId_Works()
    {
        var markdown = "<!--comment:abc123-xyz-->text<!--/comment:abc123-xyz-->";

        // Note: Our regex uses [^-]+ which stops at hyphen
        // So IDs should not contain hyphens
        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        // This won't match because of the hyphen in ID - that's expected behavior
        result.Should().Be(markdown);
    }

    [Fact]
    public void TransformAnnotations_AlphanumericId_Works()
    {
        var markdown = "<!--comment:abc123xyz-->text<!--/comment:abc123xyz-->";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Contain("data-comment-id=\"abc123xyz\"");
    }

    [Fact]
    public void TransformAnnotations_GuidId_Works()
    {
        var id = "a1b2c3d4e5f6";
        var markdown = $"<!--comment:{id}-->text<!--/comment:{id}-->";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Contain($"data-comment-id=\"{id}\"");
    }

    [Fact]
    public void TransformAnnotations_TrackChange_ProducesCleanSpan()
    {
        var markdown = "<!--insert:i1-->new text<!--/insert:i1-->";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        // Clean span without inline buttons (buttons are in side panel)
        result.Should().Contain("<span class=\"track-insert\" data-change-id=\"i1\">");
        result.Should().Contain(">new text</span>");
        result.Should().NotContain("annotation-margin-label");
    }

    [Fact]
    public void TransformAnnotations_Comment_ProducesCleanSpan()
    {
        var markdown = "<!--comment:c1-->text<!--/comment:c1-->";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        // Clean span without inline buttons
        result.Should().Contain("<span class=\"comment-highlight\" data-comment-id=\"c1\">");
        result.Should().NotContain("annotation-margin-label");
    }

    [Fact]
    public void TransformAnnotations_WithMetadata_IncludesAuthorDateAsDataAttributes()
    {
        var markdown = "<!--insert:i1:Alice:Dec 15-->new text<!--/insert:i1-->";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Contain("data-author=\"Alice\"");
        result.Should().Contain("data-date=\"Dec 15\"");
    }

    [Fact]
    public void TransformAnnotations_CommentWithText_IncludesCommentTextAsDataAttribute()
    {
        var markdown = "<!--comment:c1:Alice:Dec 15|This is my comment-->highlighted text<!--/comment:c1-->";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Contain("class=\"comment-highlight\"");
        result.Should().Contain("data-comment-id=\"c1\"");
        result.Should().Contain("data-author=\"Alice\"");
        result.Should().Contain("data-date=\"Dec 15\"");
        result.Should().Contain("data-comment-text=\"This is my comment\"");
        result.Should().Contain(">highlighted text</span>");
    }

    [Fact]
    public void TransformAnnotations_CommentWithoutText_NoCommentTextAttribute()
    {
        var markdown = "<!--comment:c1:Alice:Dec 15-->highlighted text<!--/comment:c1-->";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().NotContain("data-comment-text");
    }

    [Fact]
    public void TransformAnnotations_Comment_DataAttributesOnly()
    {
        var markdown = "<!--comment:c1:Alice:Dec 15|My comment-->text<!--/comment:c1-->";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        // All info as data attributes, no inline buttons
        result.Should().Contain("data-comment-id=\"c1\"");
        result.Should().Contain("data-author=\"Alice\"");
        result.Should().Contain("data-date=\"Dec 15\"");
        result.Should().Contain("data-comment-text=\"My comment\"");
        result.Should().NotContain("resolve-btn");
    }

    [Fact]
    public void TransformAnnotations_SimpleComment_CleanOutput()
    {
        var markdown = "<!--comment:c1-->text<!--/comment:c1-->";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Be("<span class=\"comment-highlight\" data-comment-id=\"c1\">text</span>");
    }

    #endregion

    #region AnnotationParser.ExtractAnnotations Tests

    [Fact]
    public void ExtractAnnotations_SimpleComment_ExtractsCorrectly()
    {
        var markdown = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        var result = AnnotationParser.ExtractAnnotations(markdown);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("c1");
        result[0].Type.Should().Be(MarkdownAnnotationType.Comment);
        result[0].HighlightedText.Should().Be("world");
        result[0].Author.Should().BeNull();
    }

    [Fact]
    public void ExtractAnnotations_CommentWithMetadata_ExtractsAuthorAndDate()
    {
        var markdown = "Hello <!--comment:c1:Alice:Dec 15-->world<!--/comment:c1-->!";

        var result = AnnotationParser.ExtractAnnotations(markdown);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("c1");
        result[0].Author.Should().Be("Alice");
        result[0].Date.Should().Be("Dec 15");
        result[0].HighlightedText.Should().Be("world");
    }

    [Fact]
    public void ExtractAnnotations_CommentWithText_ExtractsCommentText()
    {
        var markdown = "Hello <!--comment:c1:Alice:Dec 15|This is my comment-->world<!--/comment:c1-->!";

        var result = AnnotationParser.ExtractAnnotations(markdown);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("c1");
        result[0].CommentText.Should().Be("This is my comment");
        result[0].HighlightedText.Should().Be("world");
    }

    [Fact]
    public void ExtractAnnotations_InsertMarker_ExtractsAsInsertion()
    {
        var markdown = "Hello <!--insert:i1:Bob:Dec 16-->new text<!--/insert:i1--> world";

        var result = AnnotationParser.ExtractAnnotations(markdown);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("i1");
        result[0].Type.Should().Be(MarkdownAnnotationType.Insert);
        result[0].Author.Should().Be("Bob");
        result[0].HighlightedText.Should().Be("new text");
    }

    [Fact]
    public void ExtractAnnotations_DeleteMarker_ExtractsAsDeletion()
    {
        var markdown = "Hello <!--delete:d1:Carol:Dec 17-->old text<!--/delete:d1--> world";

        var result = AnnotationParser.ExtractAnnotations(markdown);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("d1");
        result[0].Type.Should().Be(MarkdownAnnotationType.Delete);
        result[0].Author.Should().Be("Carol");
        result[0].HighlightedText.Should().Be("old text");
    }

    [Fact]
    public void ExtractAnnotations_MultipleAnnotations_ExtractsAll()
    {
        var markdown = @"<!--comment:c1:Alice:Dec 15|Comment here-->commented<!--/comment:c1-->
                         <!--insert:i1:Bob:Dec 16-->inserted<!--/insert:i1-->
                         <!--delete:d1:Carol:Dec 17-->deleted<!--/delete:d1-->";

        var result = AnnotationParser.ExtractAnnotations(markdown);

        result.Should().HaveCount(3);
        result.Should().Contain(a => a.Id == "c1" && a.Type == MarkdownAnnotationType.Comment);
        result.Should().Contain(a => a.Id == "i1" && a.Type == MarkdownAnnotationType.Insert);
        result.Should().Contain(a => a.Id == "d1" && a.Type == MarkdownAnnotationType.Delete);
    }

    [Fact]
    public void ExtractAnnotations_EmptyContent_ReturnsEmptyList()
    {
        var result = AnnotationParser.ExtractAnnotations(string.Empty);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractAnnotations_NoAnnotations_ReturnsEmptyList()
    {
        var markdown = "Just plain text with no annotations.";

        var result = AnnotationParser.ExtractAnnotations(markdown);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractAnnotations_OrderedByPosition()
    {
        var markdown = "First <!--delete:d1-->deleted<!--/delete:d1--> then <!--comment:c1-->commented<!--/comment:c1--> last <!--insert:i1-->inserted<!--/insert:i1-->";

        var result = AnnotationParser.ExtractAnnotations(markdown);

        result.Should().HaveCount(3);
        result[0].Id.Should().Be("d1"); // First in document
        result[1].Id.Should().Be("c1"); // Second
        result[2].Id.Should().Be("i1"); // Third
    }

    #endregion
}
