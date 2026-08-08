#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The registry's REST output (<see cref="PluginRegistryPayloads"/>, written by the
/// <c>/api/plugins</c> endpoints) must be EXACTLY what the consumer's
/// <see cref="RegistryPackageSource"/> parses — otherwise a consumer browses an empty catalog while
/// the registry is fine. This pins the wire shapes so producer and consumer can't drift: it
/// round-trips the payloads through the SAME <c>{ packages }</c> / <c>{ files }</c> envelopes the
/// source deserializes.
/// </summary>
public class PluginRegistryPayloadsTest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record ListEnvelope(IReadOnlyList<PackageManifest>? Packages);
    private sealed record FilesEnvelope(IReadOnlyList<PackageFile>? Files);

    [Fact]
    public void List_RoundTrips_ThroughThePackagesEnvelope()
    {
        var manifests = new List<PackageManifest>
        {
            new() { Id = "slides", Name = "Slides", Description = "Deck node type", Kind = PackageKind.Code,
                    TargetPartition = "Slides", Version = "1.2.0", SourceFolder = "catalog/slides" },
            new() { Id = "welcome-note", Name = "Welcome", Kind = PackageKind.Content,
                    TargetPartition = "Doc", Version = "1.0.0", SourceFolder = "catalog/welcome-note" },
        };

        var json = PluginRegistryPayloads.List(manifests);
        var parsed = JsonSerializer.Deserialize<ListEnvelope>(json, Json);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Packages);
        Assert.Equal(2, parsed.Packages!.Count);

        var slides = parsed.Packages.Single(p => p.Id == "slides");
        Assert.Equal("Slides", slides.Name);
        Assert.Equal(PackageKind.Code, slides.Kind);
        Assert.Equal("Slides", slides.TargetPartition);
        Assert.Equal("1.2.0", slides.Version);
        Assert.Equal("catalog/slides", slides.SourceFolder);
    }

    [Fact]
    public void Files_RoundTrip_ThroughTheFilesEnvelope()
    {
        var files = new List<PackageFile>
        {
            new("catalog/slides/package.json", """{"id":"slides"}"""),
            new("catalog/slides/Source/Slide.cs", "public record Slide;"),
        };

        var json = PluginRegistryPayloads.Files(files);
        var parsed = JsonSerializer.Deserialize<FilesEnvelope>(json, Json);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Files);
        Assert.Equal(2, parsed.Files!.Count);

        var source = parsed.Files.Single(f => f.RelativePath == "catalog/slides/Source/Slide.cs");
        Assert.Equal("public record Slide;", source.Content);
    }

    /// <summary>
    /// 🚨 The gap issue #848 measured: <c>POST /api/plugins/files</c> answered every binary with
    /// <c>content = 0 chars</c>, so a merged course video was never published and had to be uploaded
    /// to each portal by hand. A binary file's bytes must survive the registry envelope (base64,
    /// which <c>System.Text.Json</c> does natively for <c>byte[]</c>) — and a text file must still
    /// round-trip through <c>Content</c> exactly as before, with NO stray binary field.
    /// </summary>
    [Fact]
    public void Files_RoundTrip_BinaryBytes_AndLeaveTextUntouched()
    {
        // Deliberately NOT valid UTF-8 (0x00, 0xFF, a PNG signature): a string round-trip mangles
        // these, which is why RepoFileCodec classifies such a blob as binary in the first place.
        byte[] video = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0xFF, 0xFE, 0x89, 0x50, 0x4E, 0x47];
        var files = new List<PackageFile>
        {
            new("Course/content/videos/clip.mp4", "", video),
            new("Course/Lesson.md", "# Lesson\n\nWatch the clip."),
        };

        var json = PluginRegistryPayloads.Files(files);
        var parsed = JsonSerializer.Deserialize<FilesEnvelope>(json, Json);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Files);

        var clip = parsed.Files!.Single(f => f.RelativePath == "Course/content/videos/clip.mp4");
        Assert.True(clip.IsBinary, "a committed video must arrive as bytes, not as an empty string");
        Assert.Equal(video, clip.Bytes);

        var lesson = parsed.Files.Single(f => f.RelativePath == "Course/Lesson.md");
        Assert.Equal("# Lesson\n\nWatch the clip.", lesson.Content);
        Assert.False(lesson.IsBinary);
        Assert.Null(lesson.Binary);

        // The computed helpers are [JsonIgnore]d — otherwise every response would carry a second
        // base64 copy of each binary. Pin it: the payload names `binary` and nothing else byte-ish.
        Assert.Contains("\"binary\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"bytes\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"isBinary\":", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A payload from an OLD registry — one that predates the binary field — must still parse, with
    /// <see cref="PackageFile.Binary"/> null. That is what lets registry and consumer roll
    /// independently instead of in lockstep.
    /// </summary>
    [Fact]
    public void Files_FromARegistryWithoutTheBinaryField_ParseAsText()
    {
        const string legacy = """{"files":[{"relativePath":"Course/Lesson.md","content":"# Lesson"}]}""";

        var parsed = JsonSerializer.Deserialize<FilesEnvelope>(legacy, Json);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Files);

        var lesson = parsed.Files!.Single();
        Assert.Equal("# Lesson", lesson.Content);
        Assert.Null(lesson.Binary);
        Assert.Equal("# Lesson"u8.ToArray(), lesson.Bytes);
    }

    [Fact]
    public void EmptyCatalog_RoundTrips_ToEmptyList()
    {
        var parsed = JsonSerializer.Deserialize<ListEnvelope>(PluginRegistryPayloads.List([]), Json);
        Assert.NotNull(parsed!.Packages);
        Assert.Empty(parsed.Packages!);
    }
}
