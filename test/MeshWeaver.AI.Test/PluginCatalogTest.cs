#pragma warning disable CS1591

using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Covers the plugin-registry verbs on <see cref="MeshOperations"/> (<c>Catalog</c> /
/// <c>CatalogDownload</c>) that back the <c>/api/mesh/catalog*</c> REST surface: memex is the
/// distribution point — it lists every partition that ships NodeTypes as an installable plugin and
/// hands out a plugin's DEFINITION (Space + NodeType + Source/Test Code + docs, NOT data instances
/// or runtime satellites) so a consumer imports it via <c>/update</c> with no GitHub creds.
/// </summary>
public class PluginCatalogTest : MonolithMeshTestBase
{
    public PluginCatalogTest(ITestOutputHelper output) : base(output) { }

    protected override bool ShareMeshAcrossTests => true;

    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData-plugincatalog");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder.UseMonolithMesh().AddFileSystemPersistence(TestDataPath).AddGraph().AddAI();

    [Fact]
    public async Task Catalog_ListsPlugin_AndDownloadShipsDefinitionNotInstances()
    {
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Seed a plugin: a NodeType + its Source code, plus an INSTANCE of that type (which must be
        // excluded from the download — the registry ships the capability, not the data).
        await mesh.CreateNode(new MeshNode("Widget", "WidgetKit")
        {
            NodeType = "NodeType",
            Name = "Widget",
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Description = "A widget.",
                Configuration = "config => config.AddDefaultLayoutAreas()",
                IncludeGlobalTypes = true,
            },
        }).FirstAsync().ToTask();

        await mesh.CreateNode(new MeshNode("WidgetContent", "WidgetKit/Widget/Source")
        {
            NodeType = "Code",
            Name = "WidgetContent",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration
            {
                Code = "public record WidgetContent { public string Name { get; init; } = \"\"; }",
                Language = "csharp",
            },
        }).FirstAsync().ToTask();

        await mesh.CreateNode(new MeshNode("acme-widget", "WidgetKit/Widget")
        {
            NodeType = "WidgetKit/Widget",   // an instance carries the type PATH as its nodeType
            Name = "ACME Widget",
            State = MeshNodeState.Active,
        }).FirstAsync().ToTask();

        var ops = new MeshOperations(Mesh);

        // Catalog: the partition that ships the NodeType appears (query index is eventually consistent
        // → poll on the emission shape, never a fixed delay).
        var listed = await PollUntil(() => ops.Catalog(), s => s.Contains("WidgetKit"));
        listed.Should().Contain("WidgetKit");
        listed.Should().Contain("WidgetKit/Widget");

        // Download: Space/NodeType/Code shipped; the Source code text travels; the data INSTANCE does not.
        var pkg = await PollUntil(() => ops.CatalogDownload("WidgetKit"), s => s.Contains("WidgetContent"));
        pkg.Should().Contain("WidgetKit/Widget");
        pkg.Should().Contain("public record WidgetContent");
        pkg.Should().NotContain("acme-widget",
            because: "the registry ships the plugin's DEFINITION (types + code + docs), not data instances of it");
    }

    [Fact]
    public async Task CatalogDownload_UnknownPlugin_ReturnsError()
    {
        var ops = new MeshOperations(Mesh);
        var result = await ops.CatalogDownload($"NoSuchPlugin{Guid.NewGuid():N}").FirstAsync().ToTask();
        result.Should().StartWith("Error:",
            because: "an empty plugin yields an explicit error, not an empty success");
    }

    // Poll the reactive op until the result satisfies the predicate — the sanctioned wait for an
    // eventually-consistent query index (WritingTests.md), never a fixed sleep.
    private static Task<string> PollUntil(Func<IObservable<string>> op, Func<string, bool> ok, int timeoutSeconds = 20) =>
        Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => op())
            .Where(ok)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(timeoutSeconds))
            .ToTask();
}
