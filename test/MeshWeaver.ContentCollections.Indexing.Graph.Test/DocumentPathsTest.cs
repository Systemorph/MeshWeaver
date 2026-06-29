using MeshWeaver.ContentCollections.Indexing;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Graph.Test;

/// <summary>
/// Unit tests for <see cref="DocumentPaths"/>: the per-file Document node path must be
/// DETERMINISTIC (same inputs → same path, so a re-index updates the same node) and url-safe
/// (no character that would fork the path into extra mesh segments).
/// </summary>
public class DocumentPathsTest
{
    [Fact]
    public void For_IsDeterministic_SameInputsSamePath()
    {
        var a = DocumentPaths.For("rbuergi/MyContent", "docs/report.pdf");
        var b = DocumentPaths.For("rbuergi/MyContent", "docs/report.pdf");
        a.Should().Be(b);
    }

    [Fact]
    public void For_PlacesDocumentUnderReservedSubNamespace()
    {
        var path = DocumentPaths.For("rbuergi/MyContent", "report.pdf");
        path.Should().StartWith("rbuergi/MyContent/_Documents/");
    }

    [Fact]
    public void For_ProducesExactlyOneSegmentForTheFileSlug()
    {
        // The file path's own directory separators must NOT leak into the mesh path as extra
        // segments — the whole file path collapses into ONE slug segment under _Documents.
        var path = DocumentPaths.For("rbuergi/MyContent", "a/b/c/deep file.pdf");
        var afterDocuments = path["rbuergi/MyContent/_Documents/".Length..];
        afterDocuments.Should().NotContain("/", "the file slug is a single path segment");
        afterDocuments.Should().NotContain(" ", "whitespace must be replaced for url-safety");
    }

    [Fact]
    public void For_DistinctFilesGetDistinctPaths()
    {
        var a = DocumentPaths.For("c", "report-2024.pdf");
        var b = DocumentPaths.For("c", "report-2025.pdf");
        a.Should().NotBe(b);
    }

    [Fact]
    public void For_TrailingCollectionSlashIsNormalized()
    {
        DocumentPaths.For("c/", "f.txt").Should().Be(DocumentPaths.For("c", "f.txt"));
    }

    [Theory]
    [InlineData("simple.txt", "simple.txt")]
    [InlineData("dir/sub/file.md", "dir-sub-file.md")]
    [InlineData("has spaces.docx", "has-spaces.docx")]
    [InlineData("weird&chars!@#.pdf", "weird-chars-.pdf")]
    [InlineData("/leading/slash.txt", "leading-slash.txt")]
    public void Slug_IsUrlSafeAndReadable(string filePath, string expected)
    {
        DocumentPaths.Slug(filePath).Should().Be(expected);
    }

    [Fact]
    public void Slug_OnlySeparators_FallsBackToNonEmptySegment()
    {
        // Degenerate all-separator input must still yield a non-empty, url-safe segment.
        DocumentPaths.Slug("///").Should().Be("document");
    }

    [Fact]
    public void Slug_PreservesCase()
    {
        // Mesh paths are case-sensitive; two files differing only by case are distinct.
        DocumentPaths.Slug("Report.PDF").Should().Be("Report.PDF");
        DocumentPaths.Slug("Report.PDF").Should().NotBe(DocumentPaths.Slug("report.pdf"));
    }
}
