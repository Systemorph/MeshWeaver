using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for <see cref="CommentRendering"/> — capturing a selection as a clean-text range, recomputing
/// the effective range against a newer document version, and injecting the inline highlight span.
/// </summary>
public class CommentRenderingTest
{
    private static Comment Capture(string clean, string selected, long version)
    {
        var (start, length) = CommentRendering.Capture(clean, null, null, selected);
        start.Should().BeGreaterThanOrEqualTo(0, "the selection should be locatable");
        return new Comment
        {
            MarkerId = "c1",
            HighlightedText = selected,
            Start = start,
            Length = length,
            AnchorText = clean,
            Version = version
        };
    }

    [Fact(Timeout = 5000)]
    public void Capture_LocatesPlainSelection()
    {
        const string doc = "The mesh stores nodes per partition.";
        var (start, length) = CommentRendering.Capture(doc, null, null, "stores nodes");
        doc.Substring(start, length).Should().Be("stores nodes");
    }

    [Fact(Timeout = 5000)]
    public void Capture_ReturnsNegative_WhenTextAbsent()
    {
        var (start, _) = CommentRendering.Capture("hello world", null, null, "not present");
        start.Should().Be(-1);
    }

    [Fact(Timeout = 5000)]
    public void ResolveEffective_SameVersion_UsesCapturedRange()
    {
        const string doc = "The mesh stores nodes per partition.";
        var comment = Capture(doc, "stores nodes", version: 7);

        var resolved = CommentRendering.ResolveEffective(comment, doc, currentVersion: 7);

        resolved.EffectiveStart.Should().Be(comment.Start);
        resolved.EffectiveEnd.Should().Be(comment.Start + comment.Length);
        resolved.EffectiveVersion.Should().Be(7);
        doc.Substring(resolved.EffectiveStart, resolved.EffectiveEnd - resolved.EffectiveStart)
            .Should().Be("stores nodes");
    }

    [Fact(Timeout = 5000)]
    public void ResolveEffective_DocumentAhead_RecomputesRange()
    {
        const string atV1 = "The mesh stores nodes per partition.";
        var comment = Capture(atV1, "stores nodes", version: 1);

        const string atV2 = "Overview.\n\nThe mesh stores nodes per partition.";
        var resolved = CommentRendering.ResolveEffective(comment, atV2, currentVersion: 2);

        resolved.EffectiveVersion.Should().Be(2);
        atV2.Substring(resolved.EffectiveStart, resolved.EffectiveEnd - resolved.EffectiveStart)
            .Should().Be("stores nodes");
    }

    [Fact(Timeout = 5000)]
    public void ResolveEffective_PageLevelComment_HasNoRange()
    {
        var comment = new Comment { Text = "general remark", Start = -1 };
        var resolved = CommentRendering.ResolveEffective(comment, "any doc", currentVersion: 3);
        resolved.EffectiveStart.Should().Be(-1);
        resolved.EffectiveEnd.Should().Be(-1);
    }

    [Fact(Timeout = 5000)]
    public void ResolveEffective_LegacyWithoutAnchorText_RelocatesByHighlight()
    {
        var comment = new Comment
        {
            MarkerId = "c1",
            HighlightedText = "partition",
            Start = 0,         // bogus stale offset
            Length = 9,
            AnchorText = null  // legacy: no captured anchor
        };

        var resolved = CommentRendering.ResolveEffective(comment, "stored per partition here", currentVersion: 9);

        "stored per partition here".Substring(resolved.EffectiveStart, resolved.EffectiveEnd - resolved.EffectiveStart)
            .Should().Be("partition");
    }

    [Fact(Timeout = 5000)]
    public void DecorateInline_WrapsEffectiveRange_AndPipelineRendersSpan()
    {
        const string doc = "The mesh stores nodes per partition.";
        var comment = Capture(doc, "stores nodes", version: 1);
        var resolved = CommentRendering.ResolveEffective(comment, doc, currentVersion: 1);

        var decorated = CommentRendering.DecorateInline(doc, new[] { resolved });

        decorated.Should().Contain("<span class=\"comment-highlight\" data-comment-id=\"c1\">stores nodes</span>");
        decorated.Should().NotContain("<!--comment");

        var html = Markdig.Markdown.ToHtml(decorated);
        html.Should().Contain("data-comment-id=\"c1\"");
    }

    [Fact(Timeout = 5000)]
    public void DecorateInline_AfterEditAbove_WrapsTheRelocatedText()
    {
        const string atV1 = "The mesh stores nodes per partition.";
        var comment = Capture(atV1, "stores nodes", version: 1);

        const string atV2 = "New intro line.\n\nThe mesh stores nodes per partition.";
        var resolved = CommentRendering.ResolveEffective(comment, atV2, currentVersion: 2);
        var decorated = CommentRendering.DecorateInline(atV2, new[] { resolved });

        decorated.Should().Contain("<span class=\"comment-highlight\" data-comment-id=\"c1\">stores nodes</span>");
    }
}
