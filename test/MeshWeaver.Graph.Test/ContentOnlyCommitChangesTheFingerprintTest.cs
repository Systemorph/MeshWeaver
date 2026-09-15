using System.Linq;
using System.Text;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A commit that only touches an inline content file must change the partition fingerprint.
/// Otherwise the import matches the previous marker, short-circuits as "already imported", and
/// the file is never mirrored: an added one never lands and a removed one is never pruned.
/// MeshWeaver#4394 exposed the gap by making the synthesized Space root stable, and the GitSync
/// prune tests in MeshWeaver.Plugins caught it. See Doc/Architecture/StaticRepoImport.md.
/// </summary>
public class ContentOnlyCommitChangesTheFingerprintTest
{
    private static MeshNode Doc(string id, string ns, string body) =>
        new(id, ns) { NodeType = "Markdown", Name = id, Content = body, Version = 1 };

    private static MeshNode[] Nodes() =>
    [
        Doc("Course", "", "root"),
        Doc("Intro", "Course", "# Intro"),
    ];

    private static InlineContentFile Blob(string path, string text) =>
        new(path, Encoding.UTF8.GetBytes(text));

    private static StaticContentSync Content(params (string Path, string Text)[] files) =>
        new("Course", [.. files.Select(f => Blob(f.Path, f.Text))]);

    private static string Fingerprint(params StaticContentSync[] syncs) =>
        PartitionSourceFingerprint.Compute(Nodes(), versioned: false, contentOptions: null, syncs);

    [Fact]
    public void WithoutInlineContent_ThePartitionKeepsItsNodeOnlyFingerprint() =>
        Assert.Equal(PartitionSourceFingerprint.Compute(Nodes(), versioned: false), Fingerprint());

    [Fact]
    public void RemovingAContentFile_ChangesTheFingerprint() =>
        Assert.NotEqual(
            Fingerprint(Content(("videos/intro.bin", "video"), ("posters/p.jpg", "poster"))),
            Fingerprint(Content(("videos/intro.bin", "video"))));

    [Fact]
    public void AddingAContentFile_ChangesTheFingerprint() =>
        Assert.NotEqual(
            Fingerprint(Content(("videos/intro.bin", "video"))),
            Fingerprint(Content(("videos/intro.bin", "video"), ("posters/p.jpg", "poster"))));

    [Fact]
    public void EditingAContentFilesBytes_ChangesTheFingerprint() =>
        Assert.NotEqual(
            Fingerprint(Content(("videos/intro.bin", "video v1"))),
            Fingerprint(Content(("videos/intro.bin", "video v2"))));

    [Fact]
    public void MovingAContentFile_ChangesTheFingerprint() =>
        Assert.NotEqual(
            Fingerprint(Content(("videos/intro.bin", "video"))),
            Fingerprint(Content(("videos/renamed.bin", "video"))));

    [Fact]
    public void TheSameContent_EnumeratedInAnotherOrder_KeepsTheFingerprint() =>
        Assert.Equal(
            Fingerprint(Content(("a.bin", "a"), ("b.bin", "b"))),
            Fingerprint(Content(("b.bin", "b"), ("a.bin", "a"))));

    /// <summary>
    /// 🚨 Copilot's review of #4452: the entry path must not be a plain <c>/</c> join of node,
    /// collection, target path and file. <c>(Course/Intro, content, sub, x.bin)</c> and
    /// <c>(Course, Intro, content/sub, x.bin)</c> concatenate alike, so with equal bytes a file
    /// moved between those two targets would leave the fingerprint unchanged — the very skip this
    /// change exists to stop.
    /// </summary>
    [Fact]
    public void TwoTargetsThatWouldConcatenateAlike_StayDistinct() =>
        Assert.NotEqual(
            Fingerprint(new StaticContentSync("Course/Intro", [Blob("x.bin", "x")], "content", "sub")),
            Fingerprint(new StaticContentSync("Course", [Blob("x.bin", "x")], "Intro", "content/sub")));
}
