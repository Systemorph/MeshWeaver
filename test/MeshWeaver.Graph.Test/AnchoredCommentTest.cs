using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for <see cref="AnchoredComment.Build"/> — the one place a text SELECTION becomes a
/// comment satellite, shared by the markdown view and by <c>CommentableControl</c> (which brings
/// the same anchored comments to any rendered text: a social post, an HTML block, a stack).
///
/// <para>The point of these: anchoring is text-based and node-type-agnostic. Plain text that was
/// never markdown anchors exactly like a doc page, and a selection that cannot be located degrades
/// to an unanchored comment instead of throwing or inventing a bogus range.</para>
/// </summary>
public class AnchoredCommentTest
{
    private const string PostText =
        "Everything is going right for our team right now. But that's what's been keeping me up at night.\n\n"
        + "We're delivering full consultancy mandates in a single week now.";

    private static Comment ContentOf(MeshNode node) =>
        node.Content.Should().BeOfType<Comment>().Subject;

    /// <summary>
    /// PLAIN TEXT anchors like anything else — this is the whole generalization. The post text was
    /// never markdown and carries no annotation markers, yet the selection resolves to a real
    /// (Start, Length) range in it.
    /// </summary>
    [Fact]
    public void SelectionInPlainText_IsAnchored()
    {
        const string selected = "keeping me up at night";
        var node = AnchoredComment.Build(
            "Posts/IncredibleMomentum", PostText, selected,
            "keeping me up at", "me up at night", "Roland", "Is this the hook?", 26);

        var comment = ContentOf(node);
        comment.Start.Should().BeGreaterThanOrEqualTo(0, "the selection exists verbatim in the text");
        comment.Length.Should().Be(selected.Length);
        PostText.Substring(comment.Start, comment.Length).Should().Be(selected,
            "the captured range must address exactly the selected text in the SOURCE");
        comment.MarkerId.Should().NotBeNullOrEmpty("an anchored comment carries its marker");
        comment.HighlightedText.Should().Be(selected);
        comment.AnchorText.Should().Be(PostText, "the capture is stored with the text it was taken against");
        comment.Version.Should().Be(26, "the anchor is versioned so it can be re-resolved after edits");
    }

    /// <summary>
    /// The satellite is addressed the way every other comment is — under the node's
    /// <c>_Comment</c> partition with <c>MainNode</c> pointing back — so the node's existing
    /// (already generic) Comments area lists it with no per-node-type wiring.
    /// </summary>
    [Fact]
    public void Satellite_IsAddressedUnderTheNode()
    {
        var node = AnchoredComment.Build(
            "Posts/IncredibleMomentum", PostText, "single week", "single week", "single week",
            "Roland", "tight", 26);

        node.Namespace.Should().Be($"Posts/IncredibleMomentum/{CommentsExtensions.CommentPartition}");
        node.MainNode.Should().Be("Posts/IncredibleMomentum");
        node.NodeType.Should().Be(CommentNodeType.NodeType);
        node.Name.Should().Be("Comment by Roland");
        ContentOf(node).PrimaryNodePath.Should().Be("Posts/IncredibleMomentum");
    }

    /// <summary>
    /// A selection that is NOT in the source — rendered chrome such as an author line or a fold
    /// preview, which the wrapper renders but the node's text does not contain — must still
    /// produce a usable comment. It degrades to UNANCHORED (no marker, no range) rather than
    /// throwing or anchoring somewhere arbitrary; the comment still belongs to the node.
    /// </summary>
    [Fact]
    public void SelectionOutsideTheSource_DegradesToUnanchored()
    {
        var node = AnchoredComment.Build(
            "Posts/IncredibleMomentum", PostText, "Roland Bürgi · Founder",
            "Roland Bürgi", "· Founder", "Roland", "who is this", 26);

        var comment = ContentOf(node);
        comment.MarkerId.Should().BeNull("nothing in the source matched, so there is no anchor");
        comment.Start.Should().Be(-1);
        comment.Length.Should().Be(0);
        comment.AnchorText.Should().BeNull();
        comment.Version.Should().Be(0, "an unanchored comment is not tied to a document version");
        comment.Text.Should().Be("who is this", "the comment itself is kept — only the anchor is dropped");
        comment.PrimaryNodePath.Should().Be("Posts/IncredibleMomentum");
    }

    /// <summary>A node with no text at all cannot anchor, and must not throw.</summary>
    [Fact]
    public void EmptyAnchorText_DegradesToUnanchored()
    {
        var node = AnchoredComment.Build(
            "Posts/Empty", null, "anything", "anything", "anything", "Roland", "hm", 1);

        var comment = ContentOf(node);
        comment.MarkerId.Should().BeNull();
        comment.Start.Should().Be(-1);
        comment.Text.Should().Be("hm");
    }

    /// <summary>
    /// The anchor survives an edit ELSEWHERE in the text: the range is recomputed against the new
    /// version, which is why the capture stores AnchorText instead of writing a marker into the
    /// content. This is what makes the affordance safe for readers who may comment but not edit.
    /// </summary>
    [Fact]
    public void Anchor_SurvivesAnEditEarlierInTheText()
    {
        const string selected = "single week";
        var node = AnchoredComment.Build(
            "Posts/IncredibleMomentum", PostText, selected, selected, selected, "Roland", "fast", 26);
        var comment = ContentOf(node);

        // Someone prepends a sentence — every offset after it shifts.
        var edited = "New opening line.\n\n" + PostText;
        var resolved = CommentRendering.ResolveAll([comment], edited, 27).Single();

        resolved.EffectiveStart.Should().BeGreaterThanOrEqualTo(0, "the anchor text is still present");
        edited.Substring(resolved.EffectiveStart, resolved.EffectiveEnd - resolved.EffectiveStart)
            .Should().Be(selected, "the highlight follows the text, not the original offset");
    }
}
