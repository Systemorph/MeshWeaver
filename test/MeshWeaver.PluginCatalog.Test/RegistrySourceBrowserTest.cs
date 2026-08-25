using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using Xunit;

#pragma warning disable CS1591

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The pure half of the registry source browser (MeshWeaver#2193 §C): a package manifest lists
/// exactly its compile inputs, keyed by the node paths the imported files would have had — so a
/// browsed file and an imported node share one address — and a file the manifest does not carry
/// is simply not a source.
/// </summary>
public class RegistrySourceBrowserTest
{
    private static string Manifest(params string[] files) =>
        JsonSerializer.Serialize(new
        {
            module = "Store",
            moduleVersion = "abc",
            files = files.ToDictionary(f => f, _ => "sha"),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void SourcesOf_ListsCompileInputsOnly_KeyedByNodePath()
    {
        var sources = RegistrySourceBrowser.SourcesOf(Manifest(
            "Store/index.json",
            "Store/Plugin.json",
            "Store/Plugin/Source/PluginContent.cs",
            "Store/Plugin/Test/PluginCoverTests.cs",
            "Store/Source/Shared.cs",
            "Store/Guide.md"));

        sources.Should().HaveCount(3, "only files under Source/ or Test/ directories are compile inputs");
        sources.Select(s => s.NodePath).Should().Equal(
            "Store/Plugin/Source/PluginContent",
            "Store/Plugin/Test/PluginCoverTests",
            "Store/Source/Shared");
        sources[0].RelativePath.Should().Be("Store/Plugin/Source/PluginContent.cs");
        sources[0].FileName.Should().Be("PluginContent.cs");
    }

    [Fact]
    public void SourcesOf_NoOrBrokenManifest_ListsNothing()
    {
        RegistrySourceBrowser.SourcesOf(null).Should().BeEmpty();
        RegistrySourceBrowser.SourcesOf("").Should().BeEmpty();
        RegistrySourceBrowser.SourcesOf("{ not json").Should().BeEmpty("a broken manifest is an empty listing, never a throw");
    }

    [Fact]
    public void ToSourceFile_UsesTheInstallersPathMapping()
    {
        var file = RegistrySourceBrowser.ToSourceFile("Edu/Module/Source/CourseAppTile.cs");
        file.NodePath.Should().Be("Edu/Module/Source/CourseAppTile",
            "the node path is the file path without its extension — the installer's mapping");
        file.FileName.Should().Be("CourseAppTile.cs");
        RegistrySourceBrowser.ManifestPath("Edu").Should().Be("Edu/manifest.lock");
    }
}
