using System.Collections.Immutable;
using MeshWeaver.ContentCollections.Indexing;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Test;

public class TextChunkerTest
{
    // ── ChunkPositioned: page/position attribution ───────────────────────────

    [Fact]
    public void ChunkPositioned_PlainDocument_HasNullPageAndPosition()
    {
        var doc = ExtractedDocument.PlainText(new string('x', 250));
        var chunks = TextChunker.ChunkPositioned(doc, 100, 10);

        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(c => c.Page == null && c.Position == null);
        // The text windows match the plain Chunk() windows exactly.
        chunks.Select(c => c.Text).Should().Equal(TextChunker.Chunk(doc.Text, 100, 10));
    }

    [Fact]
    public void ChunkPositioned_AttributesToStartPage_AndUnionsBoxesOnThatPage()
    {
        // "AAAA BBBB CCCC" — AAAA/BBBB on page 1, CCCC on page 2.
        var text = "AAAA BBBB CCCC";
        var spans = ImmutableArray.Create(
            new PositionedSpan(0, 4, 1, new ChunkPosition(0.10, 0.10, 0.20, 0.05)),
            new PositionedSpan(5, 4, 1, new ChunkPosition(0.10, 0.20, 0.20, 0.05)),
            new PositionedSpan(10, 4, 2, new ChunkPosition(0.10, 0.10, 0.20, 0.05)));
        var doc = new ExtractedDocument(text, spans);

        // One window over the whole text: attributed to page 1 (where it begins); box unions the two
        // page-1 words (AAAA + BBBB) and EXCLUDES the page-2 word.
        var single = TextChunker.ChunkPositioned(doc, 100, 0);
        single.Should().ContainSingle();
        single[0].Page.Should().Be(1);
        single[0].Position!.X.Should().BeApproximately(0.10, 1e-9);
        single[0].Position!.Y.Should().BeApproximately(0.10, 1e-9);
        single[0].Position!.Width.Should().BeApproximately(0.20, 1e-9);
        single[0].Position!.Height.Should().BeApproximately(0.15, 1e-9); // 0.10..0.25
    }

    [Fact]
    public void ChunkPositioned_ChunkStartingOnLaterPage_TakesThatPage()
    {
        var text = "AAAA BBBB CCCC"; // len 14
        var spans = ImmutableArray.Create(
            new PositionedSpan(0, 4, 1, new ChunkPosition(0.10, 0.10, 0.20, 0.05)),
            new PositionedSpan(5, 4, 1, new ChunkPosition(0.10, 0.20, 0.20, 0.05)),
            new PositionedSpan(10, 4, 2, new ChunkPosition(0.30, 0.40, 0.20, 0.05)));
        var doc = new ExtractedDocument(text, spans);

        // size 9, overlap 0 → windows [0,9) and [9,14). The first is page 1 (AAAA+BBBB), the second is
        // page 2 (CCCC only) — proving per-chunk attribution across a page boundary.
        var chunks = TextChunker.ChunkPositioned(doc, 9, 0);
        chunks.Should().HaveCount(2);
        chunks[0].Page.Should().Be(1);
        chunks[1].Page.Should().Be(2);
        chunks[1].Position!.X.Should().BeApproximately(0.30, 1e-9);
        chunks[1].Position!.Y.Should().BeApproximately(0.40, 1e-9);
    }

    [Fact]
    public void EmptyText_ProducesNoChunks()
    {
        TextChunker.Chunk("", 100, 10).Should().BeEmpty();
        TextChunker.Chunk(null, 100, 10).Should().BeEmpty();
    }

    [Fact]
    public void TextShorterThanWindow_ProducesSingleChunk()
    {
        var chunks = TextChunker.Chunk("hello world", 100, 10);
        chunks.Should().ContainSingle().Which.Should().Be("hello world");
    }

    [Fact]
    public void TextEqualToWindow_ProducesSingleChunk()
    {
        var text = new string('a', 100);
        var chunks = TextChunker.Chunk(text, 100, 10);
        chunks.Should().ContainSingle().Which.Should().Be(text);
    }

    [Fact]
    public void WindowAndOverlap_SliceDeterministically()
    {
        // 26 chars, size 10, overlap 4 -> stride 6. Windows start at 0, 6, 12, 18.
        // The window at 18 is [18,26) (length 8) — it reaches the end, so chunking stops there
        // rather than emitting a redundant trailing sub-window. Hence 4 chunks, not 5.
        var text = "abcdefghijklmnopqrstuvwxyz";
        var chunks = TextChunker.Chunk(text, 10, 4);

        chunks.Should().HaveCount(4);
        chunks[0].Should().Be("abcdefghij");   // [0,10)
        chunks[1].Should().Be("ghijklmnop");   // [6,16)
        chunks[2].Should().Be("mnopqrstuv");   // [12,22)
        chunks[3].Should().Be("stuvwxyz");     // [18,26) truncated to end
    }

    [Fact]
    public void Overlap_IsExactlyTheSharedTail()
    {
        var text = "0123456789ABCDEFGHIJ"; // 20 chars
        var chunks = TextChunker.Chunk(text, 8, 3); // stride 5

        // window 0: [0,8) = "01234567", window 1: [5,13) = "56789ABC"
        chunks[0].Should().Be("01234567");
        chunks[1].Should().Be("56789ABC");

        // The 3-char overlap is the tail of chunk0 == head of chunk1.
        var tail = chunks[0].Substring(chunks[0].Length - 3);
        chunks[1].Should().StartWith(tail);
        tail.Should().Be("567");
    }

    [Fact]
    public void IsDeterministic_SameInputsSameOutput()
    {
        var text = new string('x', 5000);
        var a = TextChunker.Chunk(text, 1000, 150);
        var b = TextChunker.Chunk(text, 1000, 150);
        a.Should().Equal(b);
    }

    [Fact]
    public void ZeroOverlap_TilesWithoutSharing()
    {
        var text = "abcdefghij"; // 10 chars
        var chunks = TextChunker.Chunk(text, 4, 0);
        chunks.Should().Equal("abcd", "efgh", "ij");
    }

    [Fact]
    public void OverlapClampedBelowSize_StillTerminates()
    {
        // overlap >= size would mean stride 0 (infinite loop) without the clamp.
        var text = "abcdefghij";
        var chunks = TextChunker.Chunk(text, 4, 10);
        chunks.Should().NotBeEmpty();
        // stride clamps to 1, so windows start at every index until the end.
        chunks[0].Should().Be("abcd");
    }
}
