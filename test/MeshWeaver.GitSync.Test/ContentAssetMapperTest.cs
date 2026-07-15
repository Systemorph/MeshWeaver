using System.Text;
using MeshWeaver.GitSync;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Pins the fetch-layer classification that keeps git-committed content-collection assets
/// (<c>{node}/content/**</c>) OUT of node parsing and routes them to the content collection —
/// plus the binary-safe <see cref="RepoFile"/> that carries a video/poster's raw bytes intact
/// (the corruption that repeatedly nuked the course videos was a UTF-8 round-trip of those bytes).
/// </summary>
public class ContentAssetMapperTest
{
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF, 0x42, 0x7E];

    [Fact]
    public void RootContentAsset_OwnedByRoot_FileTailAfterContent()
    {
        var a = ContentAssetMapper.TryClassify("content/videos/module1-intro.mp4", () => Png);
        Assert.NotNull(a);
        Assert.Equal("", a!.OwnerRelativePath);
        Assert.Equal("videos/module1-intro.mp4", a.FileRelativePath);
        Assert.Equal(Png, a.Bytes);
    }

    [Fact]
    public void NestedContentAsset_OwnedByNearestNode()
    {
        var a = ContentAssetMapper.TryClassify("TDD/content/x.png", () => Png);
        Assert.NotNull(a);
        Assert.Equal("TDD", a!.OwnerRelativePath);
        Assert.Equal("x.png", a.FileRelativePath);
    }

    [Fact]
    public void DeeperContentSegment_IsJustAFolderName()
    {
        // The FIRST "content" segment wins; a second one is a plain folder in the file's path.
        var a = ContentAssetMapper.TryClassify("A/content/B/content/y.bin", () => Png);
        Assert.NotNull(a);
        Assert.Equal("A", a!.OwnerRelativePath);
        Assert.Equal("B/content/y.bin", a.FileRelativePath);
    }

    [Theory]
    [InlineData("index.json")]            // node file
    [InlineData("Module.json")]           // node file
    [InlineData("Docs/Intro.md")]         // node file
    [InlineData("content")]               // bare "content" — no file after it
    [InlineData("A/content")]             // ends at content — no file
    [InlineData("README.md")]             // display file
    public void NonAssetPaths_AreNotClassified(string path)
        {
        Assert.Null(ContentAssetMapper.TryClassify(path, () => Png));
        Assert.False(ContentAssetMapper.IsContentPath(path));
    }

    [Fact]
    public void ToContentSyncs_SingleWholeCollectionMirror_TargetsSpaceRoot_FullPaths()
    {
        var assets = new[]
        {
            new ContentAssetMapper.ContentAsset("", "videos/a.mp4", Png),
            new ContentAssetMapper.ContentAsset("", "posters/a.png", Png),
            new ContentAssetMapper.ContentAsset("TDD", "x.png", Png),
        };
        // ONE whole-collection mirror (not per-owner, which would overlap and prune nested content).
        var sync = Assert.Single(ContentAssetMapper.ToContentSyncs("Course", assets));
        Assert.Equal("Course", sync.NodePath);          // the Space root hub, where "content" resolves
        Assert.Equal("content", sync.TargetCollection);
        Assert.Equal("", sync.TargetPath);              // the collection root

        Assert.Equal(3, sync.Files.Count);
        // Each file carries its FULL collection-relative path {owner}/{file} (matches the served URL).
        Assert.Contains(sync.Files, f => f.Path == "videos/a.mp4");
        Assert.Contains(sync.Files, f => f.Path == "posters/a.png");
        Assert.Contains(sync.Files, f => f.Path == "TDD/x.png");
    }

    [Fact]
    public void ToContentSyncs_NoAssets_IsEmpty()
        => Assert.Empty(ContentAssetMapper.ToContentSyncs("Course", []));

    [Fact]
    public void RepoFile_TextAndBinary_ExposeBytes()
    {
        var text = new RepoFile("a.md", "# Hi");
        Assert.False(text.IsBinary);
        Assert.Equal(Encoding.UTF8.GetBytes("# Hi"), text.Bytes);

        var bin = new RepoFile("v.mp4", string.Empty, Png);
        Assert.True(bin.IsBinary);
        Assert.Equal(Png, bin.Bytes);
    }
}
