using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for <see cref="CommentAnchoring"/> — the mechanism that anchors a comment to a
/// rendered-text range (Version + From/ToPosition + HighlightedText) and re-derives the inline
/// highlight at render time, re-anchoring off the highlighted text when the document moved ahead.
/// </summary>
public class CommentAnchoringTest
{
    [Fact(Timeout = 5000)]
    public void FindRenderedRange_LocatesPlainSelection()
    {
        const string doc = "This document is about satellite entities and meshes.";

        var (from, to) = CommentAnchoring.FindRenderedRange(doc, null, null, "satellite entities");

        from.Should().BeGreaterThanOrEqualTo(0);
        to.Should().BeGreaterThan(from);
        doc.Substring(from, to - from).Should().Be("satellite entities");
    }

    [Fact(Timeout = 5000)]
    public void ResolveRenderedRange_UsesStoredOffsets_WhenVersionMatches()
    {
        const string plain = "abcdefg satellite entities hij";
        var comment = new Comment
        {
            HighlightedText = "satellite entities",
            FromPosition = 8,
            ToPosition = 26,
            Version = 5
        };

        var range = CommentAnchoring.ResolveRenderedRange(comment, plain, docVersion: 5);

        range.Should().Be((8, 26));
    }

    [Fact(Timeout = 5000)]
    public void ResolveRenderedRange_ReAnchors_WhenDocumentIsAhead()
    {
        // The comment was anchored at offset 8 in version 5. The document moved on to version 9 with
        // text inserted above, shifting "satellite entities" forward. It must re-anchor, not use the
        // now-stale stored offsets.
        const string plainAhead = "NEW INTRO. abcdefg satellite entities hij";
        var comment = new Comment
        {
            HighlightedText = "satellite entities",
            FromPosition = 8,
            ToPosition = 26,
            Version = 5
        };

        var range = CommentAnchoring.ResolveRenderedRange(comment, plainAhead, docVersion: 9);

        range.Should().NotBeNull();
        plainAhead.Substring(range!.Value.From, range.Value.To - range.Value.From)
            .Should().Be("satellite entities");
    }

    [Fact(Timeout = 5000)]
    public void ResolveRenderedRange_ReAnchors_WhenStoredOffsetsAreStaleAtSameVersion()
    {
        // Same version but the stored offsets no longer spell the highlight (defensive) → re-anchor.
        const string plain = "xx satellite entities";
        var comment = new Comment
        {
            HighlightedText = "satellite entities",
            FromPosition = 8,
            ToPosition = 26,
            Version = 5
        };

        var range = CommentAnchoring.ResolveRenderedRange(comment, plain, docVersion: 5);

        range.Should().Be((3, 21));
    }

    [Fact(Timeout = 5000)]
    public void ResolveRenderedRange_ReturnsNull_WhenTextIsGone()
    {
        var comment = new Comment
        {
            HighlightedText = "satellite entities",
            FromPosition = 8,
            ToPosition = 26,
            Version = 5
        };

        CommentAnchoring.ResolveRenderedRange(comment, "completely different text", docVersion: 99)
            .Should().BeNull();
    }

    [Fact(Timeout = 5000)]
    public void ResolveRenderedRange_ReturnsNull_ForPageLevelComment()
    {
        var comment = new Comment { HighlightedText = null, FromPosition = -1, ToPosition = -1 };

        CommentAnchoring.ResolveRenderedRange(comment, "any text", docVersion: 1).Should().BeNull();
    }

    [Fact(Timeout = 5000)]
    public void DecorateWithComments_WrapsHighlight_AndPipelineRendersSpan()
    {
        const string raw = "This document is about satellite entities and meshes.";
        var (from, to) = CommentAnchoring.FindRenderedRange(raw, null, null, "satellite entities");
        var comment = new Comment
        {
            MarkerId = "abc123",
            HighlightedText = "satellite entities",
            FromPosition = from,
            ToPosition = to,
            Version = 1,
            Author = ""
        };

        var decorated = CommentAnchoring.DecorateWithComments(raw, new[] { comment }, docVersion: 1);

        decorated.Should().Contain("<!--comment:abc123-->satellite entities<!--/comment:abc123-->");

        // The standard annotation pipeline turns the marker into a highlight span.
        var html = AnnotationMarkdownExtension.TransformAnnotations(decorated);
        html.Should().Contain("data-comment-id=\"abc123\"");
    }

    [Fact(Timeout = 5000)]
    public void DecorateWithComments_ReAnchors_AfterEditAboveTheComment()
    {
        const string atV1 = "Intro. satellite entities here.";
        var (from, to) = CommentAnchoring.FindRenderedRange(atV1, null, null, "satellite entities");
        var comment = new Comment
        {
            MarkerId = "m1",
            HighlightedText = "satellite entities",
            FromPosition = from,
            ToPosition = to,
            Version = 1,
            Author = ""
        };

        // Document moved to v2 with a new sentence inserted above — stored offsets are now stale.
        const string atV2 = "A new first sentence was added. Intro. satellite entities here.";
        var decorated = CommentAnchoring.DecorateWithComments(atV2, new[] { comment }, docVersion: 2);

        decorated.Should().Contain("<!--comment:m1-->satellite entities<!--/comment:m1-->");
    }
}
