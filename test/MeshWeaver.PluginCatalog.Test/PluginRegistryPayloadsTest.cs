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

    [Fact]
    public void EmptyCatalog_RoundTrips_ToEmptyList()
    {
        var parsed = JsonSerializer.Deserialize<ListEnvelope>(PluginRegistryPayloads.List([]), Json);
        Assert.NotNull(parsed!.Packages);
        Assert.Empty(parsed.Packages!);
    }
}
