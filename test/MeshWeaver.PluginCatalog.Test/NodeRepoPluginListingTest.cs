#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins the storefront listing rule of <see cref="NodeRepoPackageSource"/>: a top-level
/// <c>&lt;Plugin&gt;/index.json</c> root is a package whether it is the classic <c>Space</c> OR the
/// retyped <c>Store/Plugin</c> (both accepted during the transition — a repo may carry a mix), and
/// the storefront card fields (category/icon on the node, price/currency/poster in the content)
/// flow onto the manifest. Deep <c>index.json</c> files and other node types are never listed.
/// </summary>
public class NodeRepoPluginListingTest
{
    private static readonly IReadOnlyList<RepoFile> Repo = new List<RepoFile>
    {
        // Classic Space root — still listed (un-migrated repos keep working).
        new("Widget/index.json",
            """{"$type":"MeshNode","id":"Widget","path":"Widget","name":"Widget Plugin","nodeType":"Space","description":"A widget plugin.","content":{"$type":"PluginManifest","description":"A widget plugin."}}"""),
        // Retyped storefront root — listed WITH the card fields.
        new("AgenticEngineering/index.json",
            """{"$type":"MeshNode","id":"AgenticEngineering","path":"AgenticEngineering","name":"Agentic Engineering","nodeType":"Store/Plugin","description":"Build agents that build software.","category":"Education","icon":"<svg xmlns='http://www.w3.org/2000/svg'/>","content":{"$type":"PluginContent","description":"The full course.","price":900,"currency":"CHF","poster":"/static/AgenticEngineering/content/videos/agentic.poster.png"}}"""),
        // A free-to-browse plugin root without a price — listed, Price null.
        new("Chess/index.json",
            """{"$type":"MeshNode","id":"Chess","path":"Chess","name":"Chess","nodeType":"Store/Plugin","category":"Games","content":{"$type":"PluginContent"}}"""),
        // A deep index.json is NOT a package root.
        new("Widget/Sub/index.json",
            """{"$type":"MeshNode","id":"Sub","path":"Widget/Sub","nodeType":"Store/Plugin"}"""),
        // A top-level root of another type is NOT a package.
        new("Docs/index.json",
            """{"$type":"MeshNode","id":"Docs","path":"Docs","nodeType":"Markdown"}"""),
    };

    private static NodeRepoPackageSource Source()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-xyz", Repo));
        return new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins");
    }

    [Fact(Timeout = 60_000)]
    public async Task ListsSpaceAndStorePluginRoots_WithStorefrontFields()
    {
        var packages = await Source().ListPackages("HEAD").FirstAsync().ToTask();

        packages.Select(p => p.Id).Should().Equal("AgenticEngineering", "Chess", "Widget");

        var course = packages.Single(p => p.Id == "AgenticEngineering");
        course.Name.Should().Be("Agentic Engineering");
        course.Kind.Should().Be(PackageKind.NodeRepo);
        course.Category.Should().Be("Education");
        course.Icon.Should().Contain("<svg");
        course.Price.Should().Be(900m);
        course.Currency.Should().Be("CHF");
        course.Poster.Should().Be("/static/AgenticEngineering/content/videos/agentic.poster.png");

        var chess = packages.Single(p => p.Id == "Chess");
        chess.Category.Should().Be("Games");
        chess.Price.Should().BeNull("no price = not purchasable, still listed");

        var widget = packages.Single(p => p.Id == "Widget");
        widget.Category.Should().BeNull();
        widget.Price.Should().BeNull();
    }
}
