using System;
using System.IO;
using MeshWeaver.ContentCollections;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Tests the canonical content import (<see cref="ImportContentRequest"/>) + its fluent API
/// (<see cref="ContentImportExtensions.ImportContent"/>) — the path the static-repo import uses to
/// copy content-collection files (the assets behind <c>@@content/&lt;file&gt;</c> embeds) from an
/// embedded/source collection into a node's content collection, collection-to-collection (no disk
/// staging). A "storage" collection is mapped onto each node hub; a file is pre-placed in a source
/// folder; the fluent API copies it into the target folder on the owning node, and it must land
/// where the collection serves it.
/// </summary>
public class ContentImportTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string StorageRoot =
        Path.Combine(Path.GetTempPath(), "MeshWeaverContentImportTest", Guid.NewGuid().ToString("N"));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Pre-place a source file the import will copy.
        Directory.CreateDirectory(Path.Combine(StorageRoot, "src"));
        File.WriteAllText(Path.Combine(StorageRoot, "src", "logo.svg"),
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><circle r=\"10\"/></svg>");

        var storageConfig = new ContentCollectionConfig
        {
            Name = "storage",
            SourceType = "FileSystem",
            BasePath = StorageRoot,
            IsEditable = true,
            ExposeInChildren = true
        };

        return base.ConfigureMesh(builder)
            // Every node hub gets the "storage" collection + the content-import handler
            // (via AddContentCollections → AddContentCollectionsInfrastructure).
            .ConfigureDefaultNodeHub(config => config
                .AddContentCollections()
                .AddContentCollection(_ => storageConfig));
    }

    [Fact]
    public void ImportContent_CopiesSourceFolder_ToNodeTargetCollection()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Messaging.AccessService>();
        var nodePath = "ContentImportUser";

        // Create + import under System (as the static-repo importer does) so the partition-create
        // access guard + ImportContentRequest's [RequiresPermission(Create)] are satisfied.
        using (access.ImpersonateAsSystem())
            NodeFactory.CreateNode(
                new MeshNode(nodePath) { Name = "Content Import Test", NodeType = "User" }).Should().Emit();

        ImportContentResponse response;
        using (access.ImpersonateAsSystem())
            response = Mesh.ImportContent(nodePath)
                .From("storage", "src")     // embedded/source collection + folder
                .To("storage", "content")   // target collection + folder on the node
                .Post()
                .Should().Within(30.Seconds()).Emit();

        response.Success.Should().BeTrue($"import should succeed (error: {response.Error})");
        response.FilesImported.Should().BeGreaterThanOrEqualTo(1, "logo.svg must be copied");

        // The file must land where the collection serves it, so @@content/<file> resolves at render time.
        File.Exists(Path.Combine(StorageRoot, "content", "logo.svg"))
            .Should().BeTrue("logo.svg should be copied into the target collection folder");
        File.ReadAllText(Path.Combine(StorageRoot, "content", "logo.svg")).Should().Contain("<svg");
    }
}
