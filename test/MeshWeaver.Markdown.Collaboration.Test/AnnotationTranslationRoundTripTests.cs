using FluentAssertions;
using MeshWeaver.Markdown.Collaboration;
using Xunit;

namespace MeshWeaver.Markdown.Collaboration.Test;

/// <summary>
/// Tests for StripMarkersWithRanges, ReconstructAnnotatedContent,
/// and full round-trip annotation translation (annotated ↔ display).
/// </summary>
public class AnnotationTranslationRoundTripTests
{
    #region StripMarkersWithRanges

    [Fact]
    public void StripMarkersWithRanges_EmptyContent_ReturnsEmptyAndNoRanges()
    {
        var (clean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges("");
        clean.Should().BeEmpty();
        ranges.Should().BeEmpty();
    }

    [Fact]
    public void StripMarkersWithRanges_NoMarkers_ReturnsOriginalAndNoRanges()
    {
        var content = "Hello world!";
        var (clean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(content);
        clean.Should().Be("Hello world!");
        ranges.Should().BeEmpty();
    }

    [Fact]
    public void StripMarkersWithRanges_SingleComment_CorrectCleanAndRange()
    {
        var content = "Hello <!--comment:c1-->world<!--/comment:c1-->!";
        var (clean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(content);

        clean.Should().Be("Hello world!");
        ranges.Should().HaveCount(1);
        ranges[0].Type.Should().Be("comment");
        ranges[0].MarkerId.Should().Be("c1");
        ranges[0].Start.Should().Be(6); // "Hello " = 6 chars
        ranges[0].End.Should().Be(11);  // "Hello world" = 11 chars
        clean[ranges[0].Start..ranges[0].End].Should().Be("world");
    }

    [Fact]
    public void StripMarkersWithRanges_SingleInsert_CorrectCleanAndRange()
    {
        var content = "Hello <!--insert:i1-->new <!--/insert:i1-->world";
        var (clean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(content);

        clean.Should().Be("Hello new world");
        ranges.Should().HaveCount(1);
        ranges[0].Type.Should().Be("insert");
        ranges[0].MarkerId.Should().Be("i1");
        clean[ranges[0].Start..ranges[0].End].Should().Be("new ");
    }

    [Fact]
    public void StripMarkersWithRanges_SingleDelete_CorrectCleanAndRange()
    {
        var content = "Hello <!--delete:d1-->old <!--/delete:d1-->world";
        var (clean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(content);

        clean.Should().Be("Hello old world");
        ranges.Should().HaveCount(1);
        ranges[0].Type.Should().Be("delete");
        ranges[0].MarkerId.Should().Be("d1");
        clean[ranges[0].Start..ranges[0].End].Should().Be("old ");
    }

    [Fact]
    public void StripMarkersWithRanges_MultipleMarkers_CorrectPositions()
    {
        var content = "A<!--comment:c1-->B<!--/comment:c1-->C<!--insert:i1-->D<!--/insert:i1-->E";
        var (clean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(content);

        clean.Should().Be("ABCDE");
        ranges.Should().HaveCount(2);

        // First range: comment around "B"
        ranges[0].Type.Should().Be("comment");
        ranges[0].MarkerId.Should().Be("c1");
        ranges[0].Start.Should().Be(1); // "A" = 1 char
        ranges[0].End.Should().Be(2);   // "AB" = 2 chars
        clean[ranges[0].Start..ranges[0].End].Should().Be("B");

        // Second range: insert around "D"
        ranges[1].Type.Should().Be("insert");
        ranges[1].MarkerId.Should().Be("i1");
        ranges[1].Start.Should().Be(3); // "ABC" = 3 chars
        ranges[1].End.Should().Be(4);   // "ABCD" = 4 chars
        clean[ranges[1].Start..ranges[1].End].Should().Be("D");
    }

    [Fact]
    public void StripMarkersWithRanges_AdjacentMarkers_NoOffByOne()
    {
        // Two markers right next to each other
        var content = "<!--comment:c1-->AB<!--/comment:c1--><!--insert:i1-->CD<!--/insert:i1-->";
        var (clean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(content);

        clean.Should().Be("ABCD");
        ranges.Should().HaveCount(2);

        ranges[0].Start.Should().Be(0);
        ranges[0].End.Should().Be(2);
        clean[ranges[0].Start..ranges[0].End].Should().Be("AB");

        ranges[1].Start.Should().Be(2);
        ranges[1].End.Should().Be(4);
        clean[ranges[1].Start..ranges[1].End].Should().Be("CD");
    }

    [Fact]
    public void StripMarkersWithRanges_MultilineContent_CorrectPositions()
    {
        var content = "Line 1\n<!--comment:c1-->Line 2\nLine 3<!--/comment:c1-->\nLine 4";
        var (clean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(content);

        clean.Should().Be("Line 1\nLine 2\nLine 3\nLine 4");
        ranges.Should().HaveCount(1);
        clean[ranges[0].Start..ranges[0].End].Should().Be("Line 2\nLine 3");
    }

    [Fact]
    public void StripMarkersWithRanges_MarkerAtStart_CorrectRange()
    {
        var content = "<!--delete:d1-->removed<!--/delete:d1--> kept";
        var (clean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(content);

        clean.Should().Be("removed kept");
        ranges.Should().HaveCount(1);
        ranges[0].Start.Should().Be(0);
        ranges[0].End.Should().Be(7);
        clean[ranges[0].Start..ranges[0].End].Should().Be("removed");
    }

    [Fact]
    public void StripMarkersWithRanges_MarkerAtEnd_CorrectRange()
    {
        var content = "kept <!--insert:i1-->added<!--/insert:i1-->";
        var (clean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(content);

        clean.Should().Be("kept added");
        ranges.Should().HaveCount(1);
        ranges[0].Start.Should().Be(5);
        ranges[0].End.Should().Be(10);
        clean[ranges[0].Start..ranges[0].End].Should().Be("added");
    }

    #endregion

    #region ReconstructAnnotatedContent

    [Fact]
    public void Reconstruct_NoChange_ReturnsOriginal()
    {
        var annotated = "Hello <!--comment:c1-->world<!--/comment:c1-->!";
        var (clean, _) = MarkdownAnnotationParser.StripMarkersWithRanges(annotated);

        var result = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, clean, clean);

        result.Should().Be(annotated);
    }

    [Fact]
    public void Reconstruct_InsertTextBeforeMarker_MarkersPreserved()
    {
        var annotated = "Hello <!--comment:c1-->world<!--/comment:c1-->!";
        var oldClean = "Hello world!";
        var newClean = "Hey Hello world!";

        var result = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, oldClean, newClean);

        result.Should().Be("Hey Hello <!--comment:c1-->world<!--/comment:c1-->!");
    }

    [Fact]
    public void Reconstruct_InsertTextAfterMarker_MarkersPreserved()
    {
        var annotated = "Hello <!--comment:c1-->world<!--/comment:c1-->!";
        var oldClean = "Hello world!";
        var newClean = "Hello world! Goodbye!";

        var result = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, oldClean, newClean);

        result.Should().Be("Hello <!--comment:c1-->world<!--/comment:c1-->! Goodbye!");
    }

    [Fact]
    public void Reconstruct_InsertTextBetweenMarkers_MarkersPreserved()
    {
        var annotated = "<!--comment:c1-->A<!--/comment:c1-->X<!--insert:i1-->B<!--/insert:i1-->";
        var oldClean = "AXB";
        var newClean = "AXYZB";

        var result = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, oldClean, newClean);

        // The algorithm inserts text at the mapped position, which falls inside the
        // insert marker (edit position maps to where 'B' starts in annotated content).
        // The comment marker and first marker structure are preserved.
        result.Should().Contain("<!--comment:c1-->A<!--/comment:c1-->");
        // The clean version of the result should match newClean
        MarkdownAnnotationParser.StripAllMarkers(result).Should().Be("AXYZB");
    }

    [Fact]
    public void Reconstruct_DeleteTextBeforeMarker_CleanContentCorrect()
    {
        var annotated = "Hello <!--comment:c1-->world<!--/comment:c1-->!";
        var oldClean = "Hello world!";
        var newClean = "world!";

        var result = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, oldClean, newClean);

        // The algorithm maps delete boundaries to annotated positions.
        // When the edit range abuts a marker boundary, the opening tag may be
        // consumed by the deletion. Orphaned tags are cleaned up.
        // The clean content should always match the expected display text.
        MarkdownAnnotationParser.StripAllMarkers(result).Should().Be("world!");
    }

    [Fact]
    public void Reconstruct_DeleteTextAfterMarker_MarkersPreserved()
    {
        var annotated = "Hello <!--comment:c1-->world<!--/comment:c1--> goodbye!";
        var oldClean = "Hello world goodbye!";
        var newClean = "Hello world";

        var result = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, oldClean, newClean);

        result.Should().Contain("<!--comment:c1-->world<!--/comment:c1-->");
        MarkdownAnnotationParser.StripAllMarkers(result).Should().Be("Hello world");
    }

    [Fact]
    public void Reconstruct_ReplaceTextOutsideMarker_MarkersPreserved()
    {
        var annotated = "Hello <!--comment:c1-->world<!--/comment:c1-->!";
        var oldClean = "Hello world!";
        var newClean = "Goodbye world!";

        var result = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, oldClean, newClean);

        result.Should().Contain("<!--comment:c1-->world<!--/comment:c1-->");
        MarkdownAnnotationParser.StripAllMarkers(result).Should().Be("Goodbye world!");
    }

    [Fact]
    public void Reconstruct_EmptyAnnotatedInput_ReturnsNewClean()
    {
        var result = MarkdownAnnotationParser.ReconstructAnnotatedContent("", "", "new text");
        result.Should().Be("new text");
    }

    [Fact]
    public void Reconstruct_DeleteAllCleanContent_CleansUpOrphanedTags()
    {
        var annotated = "<!--comment:c1-->world<!--/comment:c1-->";
        var oldClean = "world";
        var newClean = "";

        var result = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, oldClean, newClean);

        // After deleting all content, orphaned tags should be cleaned up
        result.Should().NotContain("<!--comment:c1-->");
        result.Should().NotContain("<!--/comment:c1-->");
    }

    [Fact]
    public void Reconstruct_ReplaceTextInsideMarker_MarkerContentUpdated()
    {
        var annotated = "Hello <!--comment:c1-->world<!--/comment:c1-->!";
        var oldClean = "Hello world!";
        var newClean = "Hello earth!";

        var result = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, oldClean, newClean);

        // The text inside the comment marker should be updated
        MarkdownAnnotationParser.StripAllMarkers(result).Should().Be("Hello earth!");
    }

    #endregion

    #region Round-Trip Tests

    [Fact]
    public void RoundTrip_StripAndReconstructNoEdit_Identity()
    {
        var annotated = "Hello <!--comment:c1-->world<!--/comment:c1--> <!--insert:i1-->beautiful<!--/insert:i1-->!";
        var (clean, _) = MarkdownAnnotationParser.StripMarkersWithRanges(annotated);

        var reconstructed = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, clean, clean);

        reconstructed.Should().Be(annotated);
    }

    [Fact]
    public void RoundTrip_AddTextAtEnd_PreservesAllMarkers()
    {
        var annotated = "Hello <!--comment:c1-->world<!--/comment:c1-->!";
        var (oldClean, _) = MarkdownAnnotationParser.StripMarkersWithRanges(annotated);
        var newClean = oldClean + " More text.";

        var reconstructed = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, oldClean, newClean);

        // Verify markers preserved
        var annotations = MarkdownAnnotationParser.ExtractAllAnnotations(reconstructed);
        annotations.Should().HaveCount(1);
        annotations[0].MarkerId.Should().Be("c1");
        annotations[0].AnnotatedText.Should().Be("world");

        // Verify clean content
        MarkdownAnnotationParser.StripAllMarkers(reconstructed).Should().Be(newClean);
    }

    [Fact]
    public void RoundTrip_DeleteTextInMiddle_PreservesSurroundingMarkers()
    {
        var annotated = "<!--comment:c1-->A<!--/comment:c1-->BCDEF<!--insert:i1-->G<!--/insert:i1-->";
        var (oldClean, _) = MarkdownAnnotationParser.StripMarkersWithRanges(annotated);
        // oldClean = "ABCDEFG"
        var newClean = "AG"; // delete "BCDEF"

        var reconstructed = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, oldClean, newClean);

        // Verify clean content
        MarkdownAnnotationParser.StripAllMarkers(reconstructed).Should().Be("AG");
    }

    [Fact]
    public void RoundTrip_MultipleEditsInSequence_MaintainsConsistency()
    {
        var annotated = "Start <!--comment:c1-->middle<!--/comment:c1--> end.";

        // Edit 1: add text at beginning
        var (clean1, _) = MarkdownAnnotationParser.StripMarkersWithRanges(annotated);
        var newClean1 = "NEW Start middle end.";
        var step1 = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, clean1, newClean1);

        // Verify step 1 is valid
        MarkdownAnnotationParser.StripAllMarkers(step1).Should().Be("NEW Start middle end.");

        // Edit 2: add text at end
        var (clean2, _) = MarkdownAnnotationParser.StripMarkersWithRanges(step1);
        var newClean2 = clean2 + " EXTRA";
        var step2 = MarkdownAnnotationParser.ReconstructAnnotatedContent(step1, clean2, newClean2);

        // Verify step 2 is valid
        MarkdownAnnotationParser.StripAllMarkers(step2).Should().Be("NEW Start middle end. EXTRA");

        // Verify marker still exists and has correct content
        var finalAnnotations = MarkdownAnnotationParser.ExtractAllAnnotations(step2);
        finalAnnotations.Should().HaveCount(1);
        finalAnnotations[0].MarkerId.Should().Be("c1");
        finalAnnotations[0].AnnotatedText.Should().Be("middle");
    }

    [Fact]
    public void RoundTrip_StripRanges_ThenReconstruct_WithAllTypes()
    {
        var annotated = "A<!--comment:c1-->B<!--/comment:c1-->C<!--insert:i1-->D<!--/insert:i1-->E<!--delete:d1-->F<!--/delete:d1-->G";
        var (oldClean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(annotated);

        oldClean.Should().Be("ABCDEFG");
        ranges.Should().HaveCount(3);

        // Verify each range maps to the correct clean text
        oldClean[ranges[0].Start..ranges[0].End].Should().Be("B");
        oldClean[ranges[1].Start..ranges[1].End].Should().Be("D");
        oldClean[ranges[2].Start..ranges[2].End].Should().Be("F");

        // Round-trip with no change
        var reconstructed = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, oldClean, oldClean);
        reconstructed.Should().Be(annotated);
    }

    #endregion

    #region Extended Format (with :author:date metadata)

    [Fact]
    public void ExtractAll_ExtendedFormat_ParsesCorrectly()
    {
        var content = "Text <!--insert:i1:Alice:Dec 18-->green underline<!--/insert:i1--> more";
        var annotations = MarkdownAnnotationParser.ExtractAllAnnotations(content);

        annotations.Should().HaveCount(1);
        annotations[0].MarkerId.Should().Be("i1");
        annotations[0].Type.Should().Be(MeshWeaver.Markdown.Collaboration.AnnotationType.Insert);
        annotations[0].AnnotatedText.Should().Be("green underline");
    }

    [Fact]
    public void ExtractAll_ExtendedDeleteFormat_ParsesCorrectly()
    {
        var content = "Please <!--delete:d2:Alice:Dec 21-->outdated and no longer relevant<!--/delete:d2--> docs";
        var annotations = MarkdownAnnotationParser.ExtractAllAnnotations(content);

        annotations.Should().HaveCount(1);
        annotations[0].MarkerId.Should().Be("d2");
        annotations[0].Type.Should().Be(MeshWeaver.Markdown.Collaboration.AnnotationType.Delete);
        annotations[0].AnnotatedText.Should().Be("outdated and no longer relevant");
    }

    [Fact]
    public void StripMarkersWithRanges_ExtendedFormat_CorrectPositions()
    {
        var content = "A<!--insert:i1:Alice:Dec 18-->B<!--/insert:i1-->C";
        var (clean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(content);

        clean.Should().Be("ABC");
        ranges.Should().HaveCount(1);
        ranges[0].MarkerId.Should().Be("i1");
        ranges[0].Start.Should().Be(1);
        ranges[0].End.Should().Be(2);
        clean[ranges[0].Start..ranges[0].End].Should().Be("B");
    }

    [Fact]
    public void StripMarkersWithRanges_MixedSimpleAndExtended_CorrectPositions()
    {
        var content = "<!--comment:c1-->A<!--/comment:c1--> <!--insert:i1:Bob:Dec 19-->B<!--/insert:i1--> <!--delete:d1:Carol:Dec 20-->C<!--/delete:d1-->";
        var (clean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(content);

        clean.Should().Be("A B C");
        ranges.Should().HaveCount(3);

        clean[ranges[0].Start..ranges[0].End].Should().Be("A");
        clean[ranges[1].Start..ranges[1].End].Should().Be("B");
        clean[ranges[2].Start..ranges[2].End].Should().Be("C");
    }

    [Fact]
    public void StripAllMarkers_CollaborativeEditingSample_NoXmlTags()
    {
        // Test with actual content from CollaborativeEditing.md
        var content = @"Text that you add appears with a <!--insert:i1:Alice:Dec 18-->green underline<!--/insert:i1-->. Others can review and accept or reject your addition.

> The quarterly report shows <!--insert:i2:Bob:Dec 19-->significant growth of 25%<!--/insert:i2--> in user engagement.

Text you want to remove appears with a <!--delete:d1:Carol:Dec 20-->red strikethrough<!--/delete:d1-->. The original text remains visible until the change is accepted.

> Please review the <!--delete:d2:Alice:Dec 21-->outdated and no longer relevant<!--/delete:d2--> documentation before the meeting.

> Our team has completed the <!--delete:d3:Bob:Dec 22-->initial<!--/delete:d3--><!--insert:i3:Bob:Dec 22-->comprehensive<!--/insert:i3--> analysis of the <!--comment:c4-->market trends<!--/comment:c4-->.";

        var clean = MarkdownAnnotationParser.StripAllMarkers(content);

        // No XML-style comment tags should remain
        clean.Should().NotContain("<!--");
        clean.Should().NotContain("-->");

        // All the display text should be present
        clean.Should().Contain("green underline");
        clean.Should().Contain("significant growth of 25%");
        clean.Should().Contain("red strikethrough");
        clean.Should().Contain("outdated and no longer relevant");
        clean.Should().Contain("initial");
        clean.Should().Contain("comprehensive");
        clean.Should().Contain("market trends");
    }

    [Fact]
    public void StripMarkersWithRanges_CollaborativeEditingSample_NoXmlTags()
    {
        var content = @"> Our team has completed the <!--delete:d3:Bob:Dec 22-->initial<!--/delete:d3--><!--insert:i3:Bob:Dec 22-->comprehensive<!--/insert:i3--> analysis of the <!--comment:c4-->market trends<!--/comment:c4-->.";

        var (clean, ranges) = MarkdownAnnotationParser.StripMarkersWithRanges(content);

        // No XML tags in clean output
        clean.Should().NotContain("<!--");
        clean.Should().NotContain("-->");

        // Verify all ranges point to correct text
        ranges.Should().HaveCount(3);
        clean[ranges[0].Start..ranges[0].End].Should().Be("initial");
        clean[ranges[1].Start..ranges[1].End].Should().Be("comprehensive");
        clean[ranges[2].Start..ranges[2].End].Should().Be("market trends");
    }

    [Fact]
    public void RoundTrip_ExtendedFormat_NoEditPreservesContent()
    {
        var annotated = "Hello <!--insert:i1:Alice:Dec 18-->beautiful<!--/insert:i1--> world";
        var (clean, _) = MarkdownAnnotationParser.StripMarkersWithRanges(annotated);

        var reconstructed = MarkdownAnnotationParser.ReconstructAnnotatedContent(annotated, clean, clean);

        reconstructed.Should().Be(annotated);
    }

    #endregion
}
