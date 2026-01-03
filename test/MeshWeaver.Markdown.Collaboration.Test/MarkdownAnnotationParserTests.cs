using System;
using FluentAssertions;
using MeshWeaver.Markdown.Collaboration;
using Xunit;

namespace MeshWeaver.Markdown.Collaboration.Test;

public class MarkdownAnnotationParserTests
{
    #region Extract Comments Tests

    [Fact]
    public void ExtractComments_SingleComment_ExtractsCorrectly()
    {
        // Arrange
        var content = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        // Act
        var comments = MarkdownAnnotationParser.ExtractComments(content);

        // Assert
        comments.Should().HaveCount(1);
        comments[0].MarkerId.Should().Be("c1");
        comments[0].AnnotatedText.Should().Be("world");
        comments[0].Type.Should().Be(AnnotationType.Comment);
    }

    [Fact]
    public void ExtractComments_MultipleComments_ExtractsAll()
    {
        // Arrange
        var content = "<!--comment:c1-->first<!--/comment:c1--> and <!--comment:c2-->second<!--/comment:c2-->";

        // Act
        var comments = MarkdownAnnotationParser.ExtractComments(content);

        // Assert
        comments.Should().HaveCount(2);
        comments[0].MarkerId.Should().Be("c1");
        comments[0].AnnotatedText.Should().Be("first");
        comments[1].MarkerId.Should().Be("c2");
        comments[1].AnnotatedText.Should().Be("second");
    }

    [Fact]
    public void ExtractComments_NoComments_ReturnsEmpty()
    {
        // Arrange
        var content = "Just plain text without any comments.";

        // Act
        var comments = MarkdownAnnotationParser.ExtractComments(content);

        // Assert
        comments.Should().BeEmpty();
    }

    [Fact]
    public void ExtractComments_MultilineContent_ExtractsCorrectly()
    {
        // Arrange
        var content = "Start <!--comment:c1-->line one\nline two\nline three<!--/comment:c1--> end";

        // Act
        var comments = MarkdownAnnotationParser.ExtractComments(content);

        // Assert
        comments.Should().HaveCount(1);
        comments[0].AnnotatedText.Should().Be("line one\nline two\nline three");
    }

    #endregion

    #region Extract Insertions Tests

    [Fact]
    public void ExtractInsertions_SingleInsertion_ExtractsCorrectly()
    {
        // Arrange
        var content = "Hello <!--insert:i1-->new text<!--/insert:i1--> world";

        // Act
        var insertions = MarkdownAnnotationParser.ExtractInsertions(content);

        // Assert
        insertions.Should().HaveCount(1);
        insertions[0].MarkerId.Should().Be("i1");
        insertions[0].AnnotatedText.Should().Be("new text");
        insertions[0].Type.Should().Be(AnnotationType.Insert);
    }

    #endregion

    #region Extract Deletions Tests

    [Fact]
    public void ExtractDeletions_SingleDeletion_ExtractsCorrectly()
    {
        // Arrange
        var content = "Hello <!--delete:d1-->removed text<!--/delete:d1--> world";

        // Act
        var deletions = MarkdownAnnotationParser.ExtractDeletions(content);

        // Assert
        deletions.Should().HaveCount(1);
        deletions[0].MarkerId.Should().Be("d1");
        deletions[0].AnnotatedText.Should().Be("removed text");
        deletions[0].Type.Should().Be(AnnotationType.Delete);
    }

    #endregion

    #region Extract All Annotations Tests

    [Fact]
    public void ExtractAllAnnotations_MixedTypes_ExtractsAll()
    {
        // Arrange
        var content = "<!--comment:c1-->commented<!--/comment:c1--> " +
                      "<!--insert:i1-->inserted<!--/insert:i1--> " +
                      "<!--delete:d1-->deleted<!--/delete:d1-->";

        // Act
        var annotations = MarkdownAnnotationParser.ExtractAllAnnotations(content);

        // Assert
        annotations.Should().HaveCount(3);
        annotations.Should().Contain(a => a.Type == AnnotationType.Comment);
        annotations.Should().Contain(a => a.Type == AnnotationType.Insert);
        annotations.Should().Contain(a => a.Type == AnnotationType.Delete);
    }

    [Fact]
    public void ExtractAllAnnotations_OrdersByPosition()
    {
        // Arrange
        var content = "A<!--delete:d1-->D<!--/delete:d1-->B<!--comment:c1-->C<!--/comment:c1-->E";

        // Act
        var annotations = MarkdownAnnotationParser.ExtractAllAnnotations(content);

        // Assert
        annotations.Should().HaveCount(2);
        annotations[0].Type.Should().Be(AnnotationType.Delete);
        annotations[1].Type.Should().Be(AnnotationType.Comment);
    }

    #endregion

    #region Insert Marker Tests

    [Fact]
    public void InsertCommentMarker_AtPosition_InsertsCorrectSyntax()
    {
        // Arrange
        var content = "Hello world!";

        // Act
        var result = MarkdownAnnotationParser.InsertCommentMarker(content, 6, 11, "c1");

        // Assert
        result.Should().Be("Hello <!--comment:c1-->world<!--/comment:c1-->!");
    }

    [Fact]
    public void InsertInsertMarker_AtPosition_InsertsCorrectSyntax()
    {
        // Arrange
        var content = "Hello world!";

        // Act
        var result = MarkdownAnnotationParser.InsertInsertMarker(content, 6, 11, "i1");

        // Assert
        result.Should().Be("Hello <!--insert:i1-->world<!--/insert:i1-->!");
    }

    [Fact]
    public void InsertDeleteMarker_AtPosition_InsertsCorrectSyntax()
    {
        // Arrange
        var content = "Hello world!";

        // Act
        var result = MarkdownAnnotationParser.InsertDeleteMarker(content, 6, 11, "d1");

        // Assert
        result.Should().Be("Hello <!--delete:d1-->world<!--/delete:d1-->!");
    }

    [Fact]
    public void InsertMarker_AtStart_Works()
    {
        // Arrange
        var content = "Hello";

        // Act
        var result = MarkdownAnnotationParser.InsertCommentMarker(content, 0, 5, "c1");

        // Assert
        result.Should().Be("<!--comment:c1-->Hello<!--/comment:c1-->");
    }

    [Fact]
    public void InsertMarker_EmptyRange_CreatesEmptyAnnotation()
    {
        // Arrange
        var content = "Hello";

        // Act
        var result = MarkdownAnnotationParser.InsertCommentMarker(content, 2, 2, "c1");

        // Assert
        result.Should().Be("He<!--comment:c1--><!--/comment:c1-->llo");
    }

    [Fact]
    public void InsertMarker_InvalidRange_ThrowsException()
    {
        // Arrange
        var content = "Hello";

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MarkdownAnnotationParser.InsertCommentMarker(content, 10, 15, "c1"));
    }

    #endregion

    #region Remove Markers Tests

    [Fact]
    public void RemoveMarkers_KeepsAnnotatedText()
    {
        // Arrange
        var content = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        // Act
        var result = MarkdownAnnotationParser.RemoveMarkers(content, "c1");

        // Assert
        result.Should().Be("Hello world!");
    }

    [Fact]
    public void RemoveMarkers_NonExistentMarker_ReturnsUnchanged()
    {
        // Arrange
        var content = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        // Act
        var result = MarkdownAnnotationParser.RemoveMarkers(content, "nonexistent");

        // Assert
        result.Should().Be(content);
    }

    [Fact]
    public void RemoveMarkersAndContent_RemovesBoth()
    {
        // Arrange
        var content = "Hello <!--insert:i1-->extra <!--/insert:i1-->world!";

        // Act
        var result = MarkdownAnnotationParser.RemoveMarkersAndContent(content, "i1");

        // Assert
        result.Should().Be("Hello world!");
    }

    #endregion

    #region Strip All Markers Tests

    [Fact]
    public void StripAllMarkers_RemovesAllAnnotationSyntax()
    {
        // Arrange
        var content = "<!--comment:c1-->Hello<!--/comment:c1--> " +
                      "<!--insert:i1-->beautiful<!--/insert:i1--> " +
                      "<!--delete:d1-->ugly<!--/delete:d1--> world";

        // Act
        var result = MarkdownAnnotationParser.StripAllMarkers(content);

        // Assert
        result.Should().Be("Hello beautiful ugly world");
    }

    [Fact]
    public void StripAllMarkers_NoMarkers_ReturnsUnchanged()
    {
        // Arrange
        var content = "Just plain text.";

        // Act
        var result = MarkdownAnnotationParser.StripAllMarkers(content);

        // Assert
        result.Should().Be("Just plain text.");
    }

    #endregion

    #region Utility Method Tests

    [Fact]
    public void HasAnnotations_WithAnnotations_ReturnsTrue()
    {
        var content = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        MarkdownAnnotationParser.HasAnnotations(content).Should().BeTrue();
    }

    [Fact]
    public void HasAnnotations_WithoutAnnotations_ReturnsFalse()
    {
        var content = "Hello world!";

        MarkdownAnnotationParser.HasAnnotations(content).Should().BeFalse();
    }

    [Fact]
    public void CountAnnotations_ReturnsCorrectCount()
    {
        var content = "<!--comment:c1-->A<!--/comment:c1--><!--insert:i1-->B<!--/insert:i1-->";

        MarkdownAnnotationParser.CountAnnotations(content).Should().Be(2);
    }

    [Fact]
    public void GetMarkerIds_ReturnsIdsForType()
    {
        var content = "<!--comment:c1-->A<!--/comment:c1--><!--comment:c2-->B<!--/comment:c2-->" +
                      "<!--insert:i1-->C<!--/insert:i1-->";

        var commentIds = MarkdownAnnotationParser.GetMarkerIds(content, AnnotationType.Comment);
        var insertIds = MarkdownAnnotationParser.GetMarkerIds(content, AnnotationType.Insert);

        commentIds.Should().BeEquivalentTo(["c1", "c2"]);
        insertIds.Should().BeEquivalentTo(["i1"]);
    }

    [Fact]
    public void GetAnnotatedTextRange_ReturnsCorrectRange()
    {
        // Arrange
        var content = "Hello <!--comment:c1-->world<!--/comment:c1-->!";
        // Position breakdown:
        // "Hello " = 6 chars
        // "<!--comment:c1-->" = 17 chars
        // "world" = 5 chars
        // "<!--/comment:c1-->" = 18 chars
        // "!" = 1 char

        // Act
        var range = MarkdownAnnotationParser.GetAnnotatedTextRange(content, "c1");

        // Assert
        range.Should().NotBeNull();
        range!.Value.Start.Should().Be(23); // 6 + 17
        range.Value.End.Should().Be(28); // 6 + 17 + 5
        content.Substring(range.Value.Start, range.Value.End - range.Value.Start)
            .Should().Be("world");
    }

    #endregion

    #region Track Change Workflow Tests

    [Fact]
    public void AcceptInsertion_RemovesMarkersKeepsText()
    {
        // Simulate accepting an insertion (user wants to keep the inserted text)
        var content = "Hello <!--insert:i1-->beautiful <!--/insert:i1-->world!";

        // Accept = remove markers, keep text
        var result = MarkdownAnnotationParser.RemoveMarkers(content, "i1");

        result.Should().Be("Hello beautiful world!");
    }

    [Fact]
    public void RejectInsertion_RemovesMarkersAndText()
    {
        // Simulate rejecting an insertion (user wants to remove the inserted text)
        var content = "Hello <!--insert:i1-->ugly <!--/insert:i1-->world!";

        // Reject = remove markers and content
        var result = MarkdownAnnotationParser.RemoveMarkersAndContent(content, "i1");

        result.Should().Be("Hello world!");
    }

    [Fact]
    public void AcceptDeletion_RemovesMarkersAndText()
    {
        // Simulate accepting a deletion (user confirms the text should be deleted)
        var content = "Hello <!--delete:d1-->ugly <!--/delete:d1-->world!";

        // Accept deletion = remove the deleted text
        var result = MarkdownAnnotationParser.RemoveMarkersAndContent(content, "d1");

        result.Should().Be("Hello world!");
    }

    [Fact]
    public void RejectDeletion_RemovesMarkersKeepsText()
    {
        // Simulate rejecting a deletion (user wants to keep the text)
        var content = "Hello <!--delete:d1-->beautiful <!--/delete:d1-->world!";

        // Reject deletion = keep the text
        var result = MarkdownAnnotationParser.RemoveMarkers(content, "d1");

        result.Should().Be("Hello beautiful world!");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Parse_MalformedMarker_Ignored()
    {
        // Missing closing tag
        var content = "Hello <!--comment:c1-->world without closing tag";

        var comments = MarkdownAnnotationParser.ExtractComments(content);

        comments.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MismatchedIds_Ignored()
    {
        // Opening and closing IDs don't match
        var content = "Hello <!--comment:c1-->world<!--/comment:c2-->";

        var comments = MarkdownAnnotationParser.ExtractComments(content);

        comments.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NestedMarkers_ExtractsOuterFirst()
    {
        // Nested markers of same type - outer is matched first due to position ordering
        var content = "<!--comment:outer-->A<!--comment:inner-->B<!--/comment:inner-->C<!--/comment:outer-->";

        var comments = MarkdownAnnotationParser.ExtractComments(content);

        // Due to StartPosition ordering, outer comes first
        // The regex matches the first closing tag it finds, which is inner's
        // But the result is ordered by start position
        comments.Should().HaveCount(1);
        comments[0].MarkerId.Should().Be("outer");
        // The annotated text includes everything up to the first matching closing tag
        comments[0].AnnotatedText.Should().Contain("B");
    }

    [Fact]
    public void Parse_SpecialCharactersInText_ExtractsCorrectly()
    {
        var content = "<!--comment:c1-->Hello <world> & \"friends\"<!--/comment:c1-->";

        var comments = MarkdownAnnotationParser.ExtractComments(content);

        comments.Should().HaveCount(1);
        comments[0].AnnotatedText.Should().Be("Hello <world> & \"friends\"");
    }

    [Fact]
    public void Parse_EmptyAnnotatedText_ExtractsCorrectly()
    {
        var content = "Before<!--comment:c1--><!--/comment:c1-->After";

        var comments = MarkdownAnnotationParser.ExtractComments(content);

        comments.Should().HaveCount(1);
        comments[0].AnnotatedText.Should().Be(string.Empty);
    }

    #endregion
}
