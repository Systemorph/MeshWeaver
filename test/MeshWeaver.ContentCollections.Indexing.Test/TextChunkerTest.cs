using MeshWeaver.ContentCollections.Indexing;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Test;

public class TextChunkerTest
{
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
