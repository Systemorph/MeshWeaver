using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The shell side of source browsing (MeshWeaver#2193 §C): on a mesh that keeps no source nodes,
/// the Sources/Tests trees list through the browser as stand-in nodes at the imported nodes'
/// exact paths, a group filters to its own namespace root, and the Code area renders a browsed
/// file read-only — or says plainly why it cannot.
/// </summary>
public class ModuleSourceBrowsingTest
{
    private sealed class FakeBrowser(IReadOnlyList<ModuleSourceFile> files) : IModuleSourceBrowser
    {
        public IObservable<IReadOnlyList<ModuleSourceFile>> ListSources(string packageId) =>
            Observable.Return<IReadOnlyList<ModuleSourceFile>>(
                files.Where(f => ModuleSourceBrowsing.PackageOf(f.NodePath) == packageId).ToList());

        public IObservable<string?> FetchSource(string packageId, string nodePath) =>
            Observable.Return<string?>(files.Any(f => f.NodePath == nodePath) ? "// text" : null);
    }

    private static readonly IReadOnlyList<ModuleSourceFile> Files =
    [
        new("Store/Plugin/Source/PluginContent", "Store/Plugin/Source/PluginContent.cs", "PluginContent.cs"),
        new("Store/Plugin/Test/PluginCoverTests", "Store/Plugin/Test/PluginCoverTests.cs", "PluginCoverTests.cs"),
        new("Store/Core/Source/MeshQueries", "Store/Core/Source/MeshQueries.cs", "MeshQueries.cs"),
        new("Edu/Module/Source/CourseAppTile", "Edu/Module/Source/CourseAppTile.cs", "CourseAppTile.cs"),
    ];

    [Fact]
    public void Synthesize_StandsInForTheImportedNode_AtTheSamePath()
    {
        var node = ModuleSourceBrowsing.Synthesize(Files[0]);
        node.Path.Should().Be("Store/Plugin/Source/PluginContent", "same address as the imported node");
        node.Name.Should().Be("PluginContent.cs");
        node.NodeType.Should().Be("Code");
        node.Content.Should().BeNull("a stand-in carries no content — the text is fetched on open");
        ModuleSourceBrowsing.PackageOf("Store/Plugin/Source/PluginContent").Should().Be("Store");
    }

    [Fact]
    public void BrowseGroup_ListsOnlyTheGroupsOwnRoot_AndSharedRootsFromTheirPackage()
    {
        var browser = new FakeBrowser(Files);
        var own = new CodeQueryGroup("src", [], [], "Store/Plugin/Source");
        var ownNodes = NodeTypeLayoutAreas.BrowseGroup(browser, own, "Store/Plugin").Wait();
        // The type's own Source/ root lists its files and nothing from Test/ or other types.
        ownNodes.Select(n => n.Path).Should().Equal(new[] { "Store/Plugin/Source/PluginContent" });

        var shared = new CodeQueryGroup("shared", [], [], "Store/Core/Source");
        // A shared=@Store/Core/Source root lists from ITS package, whoever compiles it.
        NodeTypeLayoutAreas.BrowseGroup(browser, shared, "Edu/Module").Wait()
            .Select(n => n.Path).Should().Equal(new[] { "Store/Core/Source/MeshQueries" });

        var rootless = new CodeQueryGroup("raw", ["nodeType:Code"], ["nodeType:Code"], null);
        NodeTypeLayoutAreas.BrowseGroup(browser, rootless, "Store/Plugin").Wait()
            .Should().BeEmpty("a group with no resolvable root cannot be browsed and lists nothing");
    }

    [Fact]
    public void BrowsedSourceView_IsReadOnly_AndNamesTheFile()
    {
        var view = NodeTypeLayoutAreas.BrowsedSourceView("Store/Plugin/Source/PluginContent", "// text");
        view.Should().BeOfType<StackControl>("the heading and the editor stack vertically");
        var editor = NodeTypeLayoutAreas.BrowsedSourceEditor("// text");
        editor.Readonly.Should().Be(true, "a browsed file is never editable here — the repo is the truth");
        editor.Value.Should().Be("// text");
        editor.Language.Should().Be("csharp");
    }

    [Fact]
    public void TheShellSaysWhyWhenItCannotBrowse()
    {
        ModuleSourceBrowsing.NeedsRegistryMarkdown.Should().Contain("registry");
        ModuleSourceBrowsing.NotServedMarkdown("Edu/Module/Source/Gone").Should().Contain("Edu/Module/Source/Gone");
    }
}
