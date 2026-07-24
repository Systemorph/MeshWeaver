#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The CI manifest sidecar's parse/diff semantics: tolerant parsing (anything malformed → null →
/// the caller's legacy full-install path), manifest-to-manifest diffing, and the
/// <see cref="IPackageSource"/> default paths-overload filtering.
/// </summary>
public class ModuleManifestTest
{
    private const string Valid =
        """
        {
          "schema": "mw-manifest/1",
          "module": "Widget",
          "moduleVersion": "3fa1b2c4d5e6f708",
          "sourceCommit": "abc123",
          "files": {
            "Widget/index.json": "hash-root",
            "Widget/Thing.json": "hash-type",
            "Widget/Thing/Source/Thing.cs": "hash-src"
          }
        }
        """;

    [Fact]
    public void ParsesAValidManifest()
    {
        var m = ModuleManifest.TryParse(Valid);
        m.Should().NotBeNull();
        m!.Module.Should().Be("Widget");
        m.ModuleVersion.Should().Be("3fa1b2c4d5e6f708");
        m.SourceCommit.Should().Be("abc123");
        m.Files.Should().HaveCount(3);
        m.Files["Widget/Thing.json"].Should().Be("hash-type");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"moduleVersion":""}""")]
    [InlineData("""{"moduleVersion":"v1"}""")]
    [InlineData("""{"moduleVersion":"v1","files":[]}""")]
    [InlineData("""{"moduleVersion":"v1","files":{"a":1}}""")]
    public void MalformedInputParsesToNull(string json) =>
        ModuleManifest.TryParse(json).Should().BeNull("a bad manifest must fall back to the full install, never throw");

    [Fact]
    public void DiffAgainstNoBaselineIsEverythingChanged()
    {
        var m = ModuleManifest.TryParse(Valid)!;
        var delta = m.DiffFrom(null);
        delta.AddedOrChangedFiles.Should().Equal(m.Files.Keys);
        delta.RemovedFiles.Should().BeEmpty();
    }

    [Fact]
    public void DiffYieldsChangedAddedAndRemoved()
    {
        var m = ModuleManifest.TryParse(Valid)!;
        var baseline = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Widget/index.json"] = "hash-root",       // unchanged
            ["Widget/Thing.json"] = "hash-type-OLD",   // changed
            ["Widget/Gone.md"] = "hash-gone",          // removed
            // Widget/Thing/Source/Thing.cs absent      → added
        };
        var delta = m.DiffFrom(baseline);
        delta.AddedOrChangedFiles.Should().Equal("Widget/Thing.json", "Widget/Thing/Source/Thing.cs");
        delta.RemovedFiles.Should().Equal("Widget/Gone.md");
        delta.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void IdenticalManifestsDiffEmpty()
    {
        var m = ModuleManifest.TryParse(Valid)!;
        m.DiffFrom(m.Files).IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData("manifest.lock", true)]
    [InlineData("Widget/manifest.lock", true)]
    [InlineData("Widget/manifest.lock.json", false)]
    [InlineData("Widget/index.json", false)]
    public void RecognizesManifestPaths(string path, bool expected) =>
        ModuleManifest.IsManifestPath(path).Should().Be(expected);

    [Fact]
    public void NodePathMappingSkipsNonNodeFiles()
    {
        PackageInstaller.NodePathForFile("Widget/Thing.json").Should().Be("Widget/Thing");
        PackageInstaller.NodePathForFile("Widget/index.json").Should().Be("Widget");
        PackageInstaller.NodePathForFile("Widget/Thing/Source/Thing.cs").Should().Be("Widget/Thing/Source/Thing");
        PackageInstaller.NodePathForFile("Widget/manifest.lock").Should().BeNull();
        PackageInstaller.NodePathForFile("README.md").Should().BeNull();
        PackageInstaller.NodePathForFile("Widget/Poster/content/poster.png").Should().BeNull(
            "a removed content asset must never prune its owning node");
    }

    [Fact]
    public async Task DefaultPathsOverloadFiltersLocally()
    {
        var source = new FixedSource([
            new PackageFile("Widget/index.json", "{}"),
            new PackageFile("Widget/Thing.json", "{}"),
            new PackageFile("Widget/Notes.md", "# notes"),
        ]);
        IPackageSource asInterface = source;
        var subset = await asInterface
            .FetchPackageFiles(new PackageManifest { Id = "Widget" }, "HEAD", ["Widget/Notes.md", "Widget/Nope.md"])
            .FirstAsync();
        subset.Select(f => f.RelativePath).Should().Equal(["Widget/Notes.md"],
            "unknown paths are silently absent; everything else is filtered out");
        var all = await asInterface
            .FetchPackageFiles(new PackageManifest { Id = "Widget" }, "HEAD", null)
            .FirstAsync();
        all.Should().HaveCount(3);
    }

    private sealed class FixedSource(IReadOnlyList<PackageFile> files) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>([]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            Observable.Return(files);
    }
}
