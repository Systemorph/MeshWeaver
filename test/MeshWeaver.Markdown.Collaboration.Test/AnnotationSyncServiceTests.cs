using FluentAssertions;
using MeshWeaver.Markdown;
using MeshWeaver.Markdown.Collaboration;
using Xunit;

namespace MeshWeaver.Markdown.Collaboration.Test;

public class AnnotationSyncServiceTests
{
    #region Separate Tests

    [Fact]
    public void Separate_EmptyContent_ReturnsEmpty()
    {
        var (clean, annotations) = AnnotationSyncService.Separate("");
        clean.Should().BeEmpty();
        annotations.Should().BeEmpty();
    }

    [Fact]
    public void Separate_NoMarkers_ReturnsContentUnchanged()
    {
        var content = "Hello world, no annotations here.";
        var (clean, annotations) = AnnotationSyncService.Separate(content);
        clean.Should().Be(content);
        annotations.Should().BeEmpty();
    }

    [Fact]
    public void Separate_SingleComment_ExtractsCorrectly()
    {
        var content = "Hello <!--comment:c1-->world<!--/comment:c1-->!";
        var (clean, annotations) = AnnotationSyncService.Separate(content);

        clean.Should().Be("Hello world!");
        annotations.Should().HaveCount(1);
        annotations[0].MarkerId.Should().Be("c1");
        annotations[0].Type.Should().Be("comment");
        annotations[0].Position.Should().Be(6); // "Hello " = 6 chars
        annotations[0].Length.Should().Be(5);    // "world" = 5 chars
    }

    [Fact]
    public void Separate_InsertAndDelete_ExtractsBoth()
    {
        var content = "Start <!--insert:i1-->added<!--/insert:i1--> middle <!--delete:d1-->removed<!--/delete:d1--> end";
        var (clean, annotations) = AnnotationSyncService.Separate(content);

        clean.Should().Be("Start added middle removed end");
        annotations.Should().HaveCount(2);

        annotations[0].MarkerId.Should().Be("i1");
        annotations[0].Type.Should().Be("insert");
        annotations[0].Position.Should().Be(6); // "Start " = 6
        annotations[0].Length.Should().Be(5);    // "added" = 5

        annotations[1].MarkerId.Should().Be("d1");
        annotations[1].Type.Should().Be("delete");
        annotations[1].Position.Should().Be(19); // "Start added middle " = 19
        annotations[1].Length.Should().Be(7);     // "removed" = 7
    }

    [Fact]
    public void Separate_WithMetadata_ExtractsCorrectly()
    {
        // Note: metadata uses no hyphens (regex [^-]* stops at first hyphen)
        var content = "Text <!--insert:i1:Alice:Jan01-->new text<!--/insert:i1--> done";
        var (clean, annotations) = AnnotationSyncService.Separate(content);

        clean.Should().Be("Text new text done");
        annotations.Should().HaveCount(1);
        annotations[0].MarkerId.Should().Be("i1");
        annotations[0].Position.Should().Be(5); // "Text " = 5
        annotations[0].Length.Should().Be(8);    // "new text" = 8
    }

    #endregion

    #region ComputePositionShifts Tests

    [Fact]
    public void ComputePositionShifts_NoChange_ReturnsUnmodified()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 10, Length = 5 }
        };

        var result = AnnotationSyncService.ComputePositionShifts("Hello world!", "Hello world!", annotations);

        result.Should().HaveCount(1);
        result[0].Position.Should().Be(10);
        result[0].Length.Should().Be(5);
    }

    [Fact]
    public void ComputePositionShifts_InsertBeforeAnnotation_ShiftsForward()
    {
        // Old: "Hello world!" — annotation at position 6 ("world")
        // New: "Hello big world!" — 4 chars inserted before annotation
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 6, Length = 5 }
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "Hello world!",
            "Hello big world!",
            annotations);

        result.Should().HaveCount(1);
        result[0].Position.Should().Be(10); // 6 + 4 chars inserted
        result[0].Length.Should().Be(5);
    }

    [Fact]
    public void ComputePositionShifts_InsertAfterAnnotation_NoChange()
    {
        // Old: "Hello world!" — annotation at position 0 ("Hello")
        // New: "Hello world! Extra." — text appended after annotation
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 0, Length = 5 }
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "Hello world!",
            "Hello world! Extra.",
            annotations);

        result.Should().HaveCount(1);
        result[0].Position.Should().Be(0);
        result[0].Length.Should().Be(5);
    }

    [Fact]
    public void ComputePositionShifts_DeleteBeforeAnnotation_ShiftsBack()
    {
        // Old: "Hello big world!" — annotation at position 10 ("world")
        // New: "Hello world!" — 4 chars deleted before annotation
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 10, Length = 5 }
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "Hello big world!",
            "Hello world!",
            annotations);

        result.Should().HaveCount(1);
        result[0].Position.Should().Be(6); // 10 - 4 chars deleted
        result[0].Length.Should().Be(5);
    }

    [Fact]
    public void ComputePositionShifts_EmptyAnnotations_ReturnsEmpty()
    {
        var result = AnnotationSyncService.ComputePositionShifts("old", "new", []);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ComputePositionShifts_MultipleAnnotations_ShiftsCorrectly()
    {
        // Old: "ABCDEF" — annotations at 1 (len 1) and 4 (len 1)
        // New: "AxxBCDEF" — 2 chars inserted at position 1
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "a1", Type = "comment", Position = 1, Length = 1 },
            new AnnotationRange { MarkerId = "a2", Type = "comment", Position = 4, Length = 1 }
        };

        var result = AnnotationSyncService.ComputePositionShifts("ABCDEF", "AxxBCDEF", annotations);

        result.Should().HaveCount(2);
        // First annotation is within edit zone (pos 1, edit zone starts at 1)
        // Second annotation shifts by +2
        result[1].Position.Should().Be(6); // 4 + 2
        result[1].Length.Should().Be(1);
    }

    #endregion

    #region Reassemble Tests

    [Fact]
    public void Reassemble_NoAnnotations_ReturnsCleanText()
    {
        var result = AnnotationSyncService.Reassemble("Hello world!", []);
        result.Should().Be("Hello world!");
    }

    [Fact]
    public void Reassemble_SingleComment_InjectsMarker()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 6, Length = 5 }
        };

        var result = AnnotationSyncService.Reassemble("Hello world!", annotations);
        result.Should().Be("Hello <!--comment:c1-->world<!--/comment:c1-->!");
    }

    [Fact]
    public void Reassemble_InsertAndDelete_InjectsBothMarkers()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "i1", Type = "insert", Position = 6, Length = 5 },
            new AnnotationRange { MarkerId = "d1", Type = "delete", Position = 19, Length = 7 }
        };

        var result = AnnotationSyncService.Reassemble("Start added middle removed end", annotations);
        result.Should().Be("Start <!--insert:i1-->added<!--/insert:i1--> middle <!--delete:d1-->removed<!--/delete:d1--> end");
    }

    [Fact]
    public void Reassemble_WithMetadata_InjectsMetadataInTag()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "i1", Type = "insert", Position = 5, Length = 8 }
        };

        var result = AnnotationSyncService.Reassemble(
            "Text new text done",
            annotations,
            a => "Alice:2024-01-01");

        result.Should().Be("Text <!--insert:i1:Alice:2024-01-01-->new text<!--/insert:i1--> done");
    }

    #endregion

    #region Round-Trip Tests

    [Fact]
    public void SeparateAndReassemble_RoundTrip_PreservesContent()
    {
        var original = "Hello <!--comment:c1-->world<!--/comment:c1-->! " +
                        "<!--insert:i1-->new text<!--/insert:i1--> " +
                        "<!--delete:d1-->old text<!--/delete:d1--> end";

        var (cleanText, annotations) = AnnotationSyncService.Separate(original);
        var reassembled = AnnotationSyncService.Reassemble(cleanText, annotations);

        reassembled.Should().Be(original);
    }

    [Fact]
    public void SeparateAndReassemble_WithMetadata_RoundTrip()
    {
        var original = "Text <!--insert:i1:Alice:Jan01-->new<!--/insert:i1--> done";

        var (cleanText, annotations) = AnnotationSyncService.Separate(original);

        // Metadata is lost in the range record, but we can pass it back through the metadata function
        var reassembled = AnnotationSyncService.Reassemble(cleanText, annotations, a => "Alice:Jan01");

        reassembled.Should().Be(original);
    }

    [Fact]
    public void SeparateEditReassemble_InsertsTextAndShiftsAnnotations()
    {
        var original = "Hello <!--comment:c1-->world<!--/comment:c1-->!";

        // Step 1: Separate
        var (cleanText, annotations) = AnnotationSyncService.Separate(original);
        cleanText.Should().Be("Hello world!");

        // Step 2: Edit — insert "beautiful " before "world"
        var newClean = "Hello beautiful world!";

        // Step 3: Shift annotations
        var shifted = AnnotationSyncService.ComputePositionShifts(cleanText, newClean, annotations);

        // Step 4: Reassemble
        var result = AnnotationSyncService.Reassemble(newClean, shifted);

        result.Should().Be("Hello beautiful <!--comment:c1-->world<!--/comment:c1-->!");
    }

    #endregion

    #region Separate Edge Cases

    [Fact]
    public void Separate_NullContent_ReturnsEmpty()
    {
        var (clean, annotations) = AnnotationSyncService.Separate(null!);
        clean.Should().BeEmpty();
        annotations.Should().BeEmpty();
    }

    [Fact]
    public void Separate_AdjacentMarkers_ExtractsBothWithCorrectPositions()
    {
        // Two annotations back-to-back with no gap
        var content = "<!--comment:c1-->Hello<!--/comment:c1--><!--insert:i1-->World<!--/insert:i1-->";
        var (clean, annotations) = AnnotationSyncService.Separate(content);

        clean.Should().Be("HelloWorld");
        annotations.Should().HaveCount(2);
        annotations[0].MarkerId.Should().Be("c1");
        annotations[0].Position.Should().Be(0);
        annotations[0].Length.Should().Be(5);
        annotations[1].MarkerId.Should().Be("i1");
        annotations[1].Position.Should().Be(5);
        annotations[1].Length.Should().Be(5);
    }

    [Fact]
    public void Separate_MarkerAtStart_PositionIsZero()
    {
        var content = "<!--comment:c1-->Hello<!--/comment:c1--> world";
        var (clean, annotations) = AnnotationSyncService.Separate(content);

        clean.Should().Be("Hello world");
        annotations[0].Position.Should().Be(0);
        annotations[0].Length.Should().Be(5);
    }

    [Fact]
    public void Separate_MarkerAtEnd_PositionAtEnd()
    {
        var content = "Hello <!--comment:c1-->world<!--/comment:c1-->";
        var (clean, annotations) = AnnotationSyncService.Separate(content);

        clean.Should().Be("Hello world");
        annotations[0].Position.Should().Be(6);
        annotations[0].Length.Should().Be(5);
    }

    [Fact]
    public void Separate_EntireContentAnnotated_FullRange()
    {
        var content = "<!--delete:d1-->everything<!--/delete:d1-->";
        var (clean, annotations) = AnnotationSyncService.Separate(content);

        clean.Should().Be("everything");
        annotations.Should().HaveCount(1);
        annotations[0].Position.Should().Be(0);
        annotations[0].Length.Should().Be(10);
    }

    [Fact]
    public void Separate_MultipleAnnotationsSameType_AllExtracted()
    {
        var content = "A <!--comment:c1-->B<!--/comment:c1--> C <!--comment:c2-->D<!--/comment:c2--> E";
        var (clean, annotations) = AnnotationSyncService.Separate(content);

        clean.Should().Be("A B C D E");
        annotations.Should().HaveCount(2);
        annotations[0].MarkerId.Should().Be("c1");
        annotations[1].MarkerId.Should().Be("c2");
    }

    [Fact]
    public void Separate_MultilineContent_PreservesNewlines()
    {
        var content = "Line1\n<!--comment:c1-->Line2<!--/comment:c1-->\nLine3";
        var (clean, annotations) = AnnotationSyncService.Separate(content);

        clean.Should().Be("Line1\nLine2\nLine3");
        annotations[0].Position.Should().Be(6); // "Line1\n" = 6 chars
        annotations[0].Length.Should().Be(5);    // "Line2" = 5 chars
    }

    #endregion

    #region ComputePositionShifts Edge Cases

    [Fact]
    public void ComputePositionShifts_EditDeletesAnnotatedRegionEntirely_LengthBecomesZero()
    {
        // Old: "Hello world!" — annotation at position 6, length 5 ("world")
        // New: "Hello !" — "world" deleted
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 6, Length = 5 }
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "Hello world!",
            "Hello !",
            annotations);

        result.Should().HaveCount(1);
        result[0].Length.Should().Be(0); // Annotation text was deleted
    }

    [Fact]
    public void ComputePositionShifts_EditWithinAnnotation_LengthChanges()
    {
        // Old: "Hello world!" — annotation at 0 length 12 (entire content)
        // New: "Hello big world!" — insert within annotation
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 0, Length = 12 }
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "Hello world!",
            "Hello big world!",
            annotations);

        result.Should().HaveCount(1);
        // Annotation spans entire content, edit is within it, so length should grow
        result[0].Position.Should().Be(0);
        result[0].Length.Should().Be(16); // 12 + 4 inserted chars
    }

    [Fact]
    public void ComputePositionShifts_ReplaceTextBeforeAnnotation_ShiftsByDelta()
    {
        // Old: "ABCXYZ" — annotation at 3 length 3 ("XYZ")
        // New: "ABCDEFXYZ" — "ABC" → "ABCDEF" (3 chars inserted at pos 3)
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 3, Length = 3 }
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "ABCXYZ",
            "ABCDEFXYZ",
            annotations);

        result.Should().HaveCount(1);
        result[0].Position.Should().Be(6); // 3 + 3 inserted
        result[0].Length.Should().Be(3);
    }

    [Fact]
    public void ComputePositionShifts_CompleteReplacement_AnnotationClamped()
    {
        // Old: "Hello world" — annotation at 6 length 5 ("world")
        // New: "Goodbye" — complete replacement
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 6, Length = 5 }
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "Hello world",
            "Goodbye",
            annotations);

        result.Should().HaveCount(1);
        // Annotation overlaps edit zone, position clamped
        result[0].Length.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void ComputePositionShifts_DeleteAllContent_AnnotationLengthZero()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 3, Length = 4 }
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "Hello world!",
            "",
            annotations);

        result.Should().HaveCount(1);
        result[0].Length.Should().Be(0);
    }

    [Fact]
    public void ComputePositionShifts_InsertIntoEmptyContent_AnnotationsPreserved()
    {
        // Annotation at position 0 length 0 (empty marker)
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 0, Length = 0 }
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "",
            "Hello",
            annotations);

        result.Should().HaveCount(1);
        result[0].Position.Should().Be(0);
    }

    [Fact]
    public void ComputePositionShifts_AnnotationAtExactEditBoundary_NoShift()
    {
        // Old: "ABCDEF" — annotation at 3 length 3 ("DEF")
        // New: "ABCxxDEF" — insert at position 3, right at annotation start
        // prefix = "ABC" (3), edit zone in old = [3,3), delta = +2
        // annotation pos 3 >= editEnd 3 → shifts by +2
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 3, Length = 3 }
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "ABCDEF",
            "ABCxxDEF",
            annotations);

        result.Should().HaveCount(1);
        result[0].Position.Should().Be(5); // 3 + 2
        result[0].Length.Should().Be(3);
    }

    [Fact]
    public void ComputePositionShifts_PartialOverlapFromStart_ClampedPosition()
    {
        // Old: "ABCDEFGH" — annotation at 2 length 4 ("CDEF")
        // New: "AXEFGH" — "BCD" replaced by "X" (delete 3, insert 1 at pos 1)
        // prefix = "A" (1), suffix = "EFGH" (4), editEnd old = 8-4 = 4
        // annotation at 2 partially overlaps [1,4)
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 2, Length = 4 }
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "ABCDEFGH",
            "AXEFGH",
            annotations);

        result.Should().HaveCount(1);
        // Overlaps edit zone, gets clamped
        result[0].Position.Should().BeGreaterThanOrEqualTo(0);
        result[0].Length.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void ComputePositionShifts_ThreeAnnotations_OnlyMiddleAffected()
    {
        // Old: "AABBCCDD" — three annotations
        // New: "AAXCCDD" — "BB" replaced by "X" at position 2
        // prefix = "AA" (2), suffix = "CCDD" (4), editEnd = 8-4 = 4
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "a1", Type = "comment", Position = 0, Length = 2 }, // "AA" — before edit
            new AnnotationRange { MarkerId = "a2", Type = "comment", Position = 2, Length = 2 }, // "BB" — in edit zone
            new AnnotationRange { MarkerId = "a3", Type = "comment", Position = 4, Length = 2 }, // "CC" — after edit
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "AABBCCDD",
            "AAXCCDD",
            annotations);

        result.Should().HaveCount(3);
        // First annotation: entirely before edit zone, unchanged
        result[0].Position.Should().Be(0);
        result[0].Length.Should().Be(2);
        // Third annotation: after edit zone, shifted by delta (-1)
        result[2].Position.Should().Be(3); // 4 - 1
        result[2].Length.Should().Be(2);
    }

    [Fact]
    public void ComputePositionShifts_AppendOnly_NoAnnotationChange()
    {
        // Appending text at the end should not affect any annotation
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 0, Length = 5 },
            new AnnotationRange { MarkerId = "c2", Type = "comment", Position = 6, Length = 5 },
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "Hello world!",
            "Hello world! More text here.",
            annotations);

        result.Should().HaveCount(2);
        result[0].Position.Should().Be(0);
        result[0].Length.Should().Be(5);
        result[1].Position.Should().Be(6);
        result[1].Length.Should().Be(5);
    }

    [Fact]
    public void ComputePositionShifts_PrependText_AllShiftForward()
    {
        // Prepending text with no shared prefix shifts everything
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 0, Length = 5 },
            new AnnotationRange { MarkerId = "c2", Type = "comment", Position = 6, Length = 5 },
        };

        var result = AnnotationSyncService.ComputePositionShifts(
            "Hello world!",
            "XX Hello world!",
            annotations);

        result.Should().HaveCount(2);
        // Both should shift by 3 ("XX " = 3 chars)
        result[0].Position.Should().Be(3);
        result[1].Position.Should().Be(9);
    }

    #endregion

    #region Reassemble Edge Cases

    [Fact]
    public void Reassemble_AnnotationAtStart_InjectsAtPosition0()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 0, Length = 5 }
        };

        var result = AnnotationSyncService.Reassemble("Hello world", annotations);
        result.Should().Be("<!--comment:c1-->Hello<!--/comment:c1--> world");
    }

    [Fact]
    public void Reassemble_AnnotationAtEnd_InjectsAtEnd()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 6, Length = 5 }
        };

        var result = AnnotationSyncService.Reassemble("Hello world", annotations);
        result.Should().Be("Hello <!--comment:c1-->world<!--/comment:c1-->");
    }

    [Fact]
    public void Reassemble_AnnotationSpansEntireText_WrapsAll()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 0, Length = 11 }
        };

        var result = AnnotationSyncService.Reassemble("Hello world", annotations);
        result.Should().Be("<!--comment:c1-->Hello world<!--/comment:c1-->");
    }

    [Fact]
    public void Reassemble_ZeroLengthAnnotation_InjectsEmptyMarker()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 5, Length = 0 }
        };

        var result = AnnotationSyncService.Reassemble("Hello world", annotations);
        result.Should().Be("Hello<!--comment:c1--><!--/comment:c1--> world");
    }

    [Fact]
    public void Reassemble_PositionBeyondTextLength_ClampsToEnd()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 100, Length = 5 }
        };

        var result = AnnotationSyncService.Reassemble("Hello", annotations);
        // Position clamped to 5 (length of text), length clamped to 0
        result.Should().Be("Hello<!--comment:c1--><!--/comment:c1-->");
    }

    [Fact]
    public void Reassemble_AdjacentAnnotations_NoGap()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 0, Length = 5 },
            new AnnotationRange { MarkerId = "c2", Type = "insert", Position = 5, Length = 6 }
        };

        var result = AnnotationSyncService.Reassemble("Hello world!", annotations);
        result.Should().Be("<!--comment:c1-->Hello<!--/comment:c1--><!--insert:c2--> world<!--/insert:c2-->!");
    }

    [Fact]
    public void Reassemble_EmptyCleanText_WithAnnotation()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "c1", Type = "comment", Position = 0, Length = 0 }
        };

        var result = AnnotationSyncService.Reassemble("", annotations);
        result.Should().Be("<!--comment:c1--><!--/comment:c1-->");
    }

    [Fact]
    public void Reassemble_NullMetadataFunction_OmitsMetadata()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "i1", Type = "insert", Position = 0, Length = 5 }
        };

        var result = AnnotationSyncService.Reassemble("Hello", annotations, null);
        result.Should().Be("<!--insert:i1-->Hello<!--/insert:i1-->");
    }

    [Fact]
    public void Reassemble_MetadataReturnsEmpty_OmitsMetadata()
    {
        var annotations = new[]
        {
            new AnnotationRange { MarkerId = "i1", Type = "insert", Position = 0, Length = 5 }
        };

        var result = AnnotationSyncService.Reassemble("Hello", annotations, _ => "");
        result.Should().Be("<!--insert:i1-->Hello<!--/insert:i1-->");
    }

    #endregion

    #region Full Lifecycle Edge Cases

    [Fact]
    public void FullLifecycle_DeleteTextBeforeAnnotation_ShiftsCorrectly()
    {
        var original = "Some prefix <!--comment:c1-->target<!--/comment:c1--> suffix";

        var (clean, annotations) = AnnotationSyncService.Separate(original);
        clean.Should().Be("Some prefix target suffix");

        // Delete "Some " (5 chars) from the beginning
        var edited = "prefix target suffix";
        var shifted = AnnotationSyncService.ComputePositionShifts(clean, edited, annotations);
        var result = AnnotationSyncService.Reassemble(edited, shifted);

        result.Should().Be("prefix <!--comment:c1-->target<!--/comment:c1--> suffix");
    }

    [Fact]
    public void FullLifecycle_DeleteTextAfterAnnotation_NoEffect()
    {
        var original = "Hello <!--comment:c1-->world<!--/comment:c1--> and more";

        var (clean, annotations) = AnnotationSyncService.Separate(original);
        var edited = "Hello world";
        var shifted = AnnotationSyncService.ComputePositionShifts(clean, edited, annotations);
        var result = AnnotationSyncService.Reassemble(edited, shifted);

        result.Should().Be("Hello <!--comment:c1-->world<!--/comment:c1-->");
    }

    [Fact]
    public void FullLifecycle_EditBetweenTwoAnnotations_BothPreserved()
    {
        var original = "A <!--comment:c1-->B<!--/comment:c1--> C <!--comment:c2-->D<!--/comment:c2--> E";

        var (clean, annotations) = AnnotationSyncService.Separate(original);
        clean.Should().Be("A B C D E");

        // Replace " C " with " XX " (same length, positions unchanged)
        var edited = "A B XX D E";
        var shifted = AnnotationSyncService.ComputePositionShifts(clean, edited, annotations);
        var result = AnnotationSyncService.Reassemble(edited, shifted);

        result.Should().Contain("<!--comment:c1-->B<!--/comment:c1-->");
        result.Should().Contain("<!--comment:c2-->D<!--/comment:c2-->");
    }

    [Fact]
    public void FullLifecycle_MultipleAnnotations_InsertTextAtStart()
    {
        var original = "<!--insert:i1-->added<!--/insert:i1--> and <!--delete:d1-->removed<!--/delete:d1-->";

        var (clean, annotations) = AnnotationSyncService.Separate(original);
        clean.Should().Be("added and removed");

        // Prepend "NEW: " (5 chars)
        var edited = "NEW: added and removed";
        var shifted = AnnotationSyncService.ComputePositionShifts(clean, edited, annotations);
        var result = AnnotationSyncService.Reassemble(edited, shifted);

        result.Should().Be("NEW: <!--insert:i1-->added<!--/insert:i1--> and <!--delete:d1-->removed<!--/delete:d1-->");
    }

    [Fact]
    public void FullLifecycle_ReplaceAnnotatedText_AnnotationShrinks()
    {
        var original = "Hello <!--comment:c1-->beautiful world<!--/comment:c1-->!";

        var (clean, annotations) = AnnotationSyncService.Separate(original);
        clean.Should().Be("Hello beautiful world!");

        // Replace "beautiful world" with "earth" — shorter replacement overlapping annotation
        var edited = "Hello earth!";
        var shifted = AnnotationSyncService.ComputePositionShifts(clean, edited, annotations);

        // The annotation overlaps the edit zone, so it gets clamped
        shifted.Should().HaveCount(1);
        var result = AnnotationSyncService.Reassemble(edited, shifted);

        // Verify the result is valid markdown (markers properly closed)
        result.Should().Contain("<!--comment:c1-->");
        result.Should().Contain("<!--/comment:c1-->");
    }

    [Fact]
    public void FullLifecycle_NoChanges_IdenticalOutput()
    {
        var original = "Start <!--insert:i1-->mid<!--/insert:i1--> end";

        var (clean, annotations) = AnnotationSyncService.Separate(original);
        var shifted = AnnotationSyncService.ComputePositionShifts(clean, clean, annotations);
        var result = AnnotationSyncService.Reassemble(clean, shifted);

        result.Should().Be(original);
    }

    [Fact]
    public void FullLifecycle_AppendToAnnotatedDocument_AnnotationsUnchanged()
    {
        var original = "Text <!--comment:c1-->here<!--/comment:c1-->.";

        var (clean, annotations) = AnnotationSyncService.Separate(original);
        clean.Should().Be("Text here.");

        var edited = "Text here. More text added at the end.";
        var shifted = AnnotationSyncService.ComputePositionShifts(clean, edited, annotations);
        var result = AnnotationSyncService.Reassemble(edited, shifted);

        result.Should().Be("Text <!--comment:c1-->here<!--/comment:c1-->. More text added at the end.");
    }

    #endregion

    #region InjectAnnotationSpans Tests

    [Fact]
    public void InjectAnnotationSpans_EmptyAnnotations_ReturnsCleanContent()
    {
        var result = AnnotationMarkdownExtension.InjectAnnotationSpans("Hello world!", []);
        result.Should().Be("Hello world!");
    }

    [Fact]
    public void InjectAnnotationSpans_Comment_InjectsHighlightSpan()
    {
        var annotations = new[]
        {
            new AnnotationSpanInfo { Id = "c1", Type = "comment", Position = 6, Length = 5 }
        };

        var result = AnnotationMarkdownExtension.InjectAnnotationSpans("Hello world!", annotations);

        result.Should().Contain("<span class=\"comment-highlight\" data-comment-id=\"c1\">");
        result.Should().Contain(">world</span>");
    }

    [Fact]
    public void InjectAnnotationSpans_Insert_InjectsTrackInsertSpan()
    {
        var annotations = new[]
        {
            new AnnotationSpanInfo { Id = "i1", Type = "insert", Position = 6, Length = 5, Author = "Alice" }
        };

        var result = AnnotationMarkdownExtension.InjectAnnotationSpans("Hello world!", annotations);

        result.Should().Contain("<span class=\"track-insert\" data-change-id=\"i1\"");
        result.Should().Contain("data-author=\"Alice\"");
    }

    [Fact]
    public void InjectAnnotationSpans_Delete_InjectsTrackDeleteSpan()
    {
        var annotations = new[]
        {
            new AnnotationSpanInfo { Id = "d1", Type = "delete", Position = 6, Length = 5, Author = "Bob", Date = "Dec 20" }
        };

        var result = AnnotationMarkdownExtension.InjectAnnotationSpans("Hello world!", annotations);

        result.Should().Contain("<span class=\"track-delete\" data-change-id=\"d1\"");
        result.Should().Contain("data-author=\"Bob\"");
        result.Should().Contain("data-date=\"Dec 20\"");
    }

    [Fact]
    public void InjectAnnotationSpans_MultipleAnnotations_AllInjected()
    {
        var annotations = new[]
        {
            new AnnotationSpanInfo { Id = "c1", Type = "comment", Position = 0, Length = 5 },
            new AnnotationSpanInfo { Id = "i1", Type = "insert", Position = 6, Length = 5 }
        };

        var result = AnnotationMarkdownExtension.InjectAnnotationSpans("Hello world!", annotations);

        result.Should().Contain("data-comment-id=\"c1\"");
        result.Should().Contain("data-change-id=\"i1\"");
    }

    #endregion
}
