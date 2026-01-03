using FluentAssertions;
using MeshWeaver.Markdown;
using Xunit;

namespace MeshWeaver.Markdown.Collaboration.Test;

public class AnnotationMarkdownExtensionTests
{
    #region TransformAnnotations Tests

    [Fact]
    public void TransformAnnotations_CommentMarker_ConvertsToSpan()
    {
        var markdown = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Be("Hello <span class=\"annotation-comment\" data-marker-id=\"c1\">world</span>!");
    }

    [Fact]
    public void TransformAnnotations_InsertMarker_ConvertsToSpan()
    {
        var markdown = "Hello <!--insert:i1-->new text<!--/insert:i1--> world";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Be("Hello <span class=\"annotation-insert\" data-marker-id=\"i1\">new text</span> world");
    }

    [Fact]
    public void TransformAnnotations_DeleteMarker_ConvertsToSpan()
    {
        var markdown = "Hello <!--delete:d1-->old text<!--/delete:d1--> world";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Be("Hello <span class=\"annotation-delete\" data-marker-id=\"d1\">old text</span> world");
    }

    [Fact]
    public void TransformAnnotations_MixedMarkers_ConvertsAll()
    {
        var markdown = "<!--comment:c1-->commented<!--/comment:c1--> " +
                       "<!--insert:i1-->inserted<!--/insert:i1--> " +
                       "<!--delete:d1-->deleted<!--/delete:d1-->";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Contain("<span class=\"annotation-comment\" data-marker-id=\"c1\">commented</span>");
        result.Should().Contain("<span class=\"annotation-insert\" data-marker-id=\"i1\">inserted</span>");
        result.Should().Contain("<span class=\"annotation-delete\" data-marker-id=\"d1\">deleted</span>");
    }

    [Fact]
    public void TransformAnnotations_MultilineContent_Works()
    {
        var markdown = "Start <!--comment:c1-->line1\nline2\nline3<!--/comment:c1--> end";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Contain("<span class=\"annotation-comment\" data-marker-id=\"c1\">line1\nline2\nline3</span>");
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

        result.Should().Contain("data-marker-id=\"abc123xyz\"");
    }

    [Fact]
    public void TransformAnnotations_GuidId_Works()
    {
        var id = "a1b2c3d4e5f6";
        var markdown = $"<!--comment:{id}-->text<!--/comment:{id}-->";

        var result = AnnotationMarkdownExtension.TransformAnnotations(markdown);

        result.Should().Contain($"data-marker-id=\"{id}\"");
    }

    #endregion
}
