using MeshWeaver.AI;
using MeshWeaver.ContentCollections.Indexing;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit coverage for <see cref="MeshOperations.TryGetContentImagePath"/> — the parse that routes a
/// <c>get</c> on an image in the default <c>content</c> collection to the file's Document node (name +
/// AI description) instead of returning raw bytes. It must recognise image files in the content
/// collection (slash and legacy colon forms), and must NOT fire for non-images, other path shapes, or
/// the <c>_Documents</c> subtree itself (which would recurse).
/// </summary>
public class ContentImagePathParsingTest
{
    [Theory]
    [InlineData("AgenticPension/content/chart.png", "AgenticPension/content", "chart.png")]
    [InlineData("Space/content/sub/dir/photo.jpeg", "Space/content", "sub/dir/photo.jpeg")]
    [InlineData("A/B/content/logo.WEBP", "A/B/content", "logo.WEBP")]
    [InlineData("Space/content:diagram.gif", "Space/content", "diagram.gif")]
    public void Recognises_Image_Content_Paths(string path, string expectedCollection, string expectedFile)
    {
        MeshOperations.TryGetContentImagePath(path, out var collectionPath, out var filePath)
            .Should().BeTrue();
        collectionPath.Should().Be(expectedCollection);
        filePath.Should().Be(expectedFile);

        // The derived Document path is stable and under the collection's _Documents subtree.
        DocumentPaths.For(collectionPath, filePath)
            .Should().StartWith($"{expectedCollection}/{DocumentPaths.DocumentsSubNamespace}/");
    }

    [Theory]
    [InlineData("AgenticPension/content/contract.pdf")]   // has a transformer — not an image
    [InlineData("AgenticPension/content/notes.txt")]      // text
    [InlineData("AgenticPension/content/data.xlsx")]      // has a transformer
    [InlineData("Space/content/_Documents/chart.png")]    // the Document node itself — must not recurse
    [InlineData("Space/Files/chart.png")]                 // named (non-'content') collection — out of scope
    [InlineData("Space/chart.png")]                       // not under a collection
    [InlineData("content/chart.png")]                     // no owning node before the collection
    [InlineData("")]
    public void Ignores_Non_Image_Content_Paths(string path)
    {
        MeshOperations.TryGetContentImagePath(path, out _, out _).Should().BeFalse();
    }
}
