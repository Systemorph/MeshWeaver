#pragma warning disable CS1591

using System.Text;
using MeshWeaver.Plugin.Build;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins that a fetch REPLACES the upstream tree rather than merging into it — the property that
/// makes a package's <c>content.includeSource</c> flip actually take effect on consumers.
///
/// <para>Worth its own suite because the failure is silent and self-concealing: if a fetch merged,
/// flipping source OFF would leave yesterday's <c>Source/*.cs</c> on disk, the consumer would keep
/// compiling against code the producer deliberately withheld, and every build would stay green
/// while doing it. Nothing would report the withheld source as missing, because it would not be
/// missing.</para>
/// </summary>
public class ModuleFetchMaterialiseTest : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "mw-fetch-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static BundleReader.ContentFile Node(string path, string body) =>
        new(path, Encoding.UTF8.GetBytes(body));

    [Fact]
    public void FlippingSourceOffRemovesTheSourceAlreadyOnDisk()
    {
        var target = Path.Combine(root, "Edu");

        // Release 1 ships source (the package opted in).
        ModuleFetchCommand.Materialise(
        [
            Node("index.json", """{"nodeType":"Store/Plugin"}"""),
            Node("Module.json", """{"nodeType":"NodeType"}"""),
            Node("Module/Source/ModuleContent.cs", "public record ModuleContent;"),
        ], target);

        Assert.True(File.Exists(Path.Combine(target, "Module", "Source", "ModuleContent.cs")));

        // Release 2 withholds it — content.includeSource flipped to false, so the bundle simply
        // does not declare the .cs files any more.
        ModuleFetchCommand.Materialise(
        [
            Node("index.json", """{"nodeType":"Store/Plugin"}"""),
            Node("Module.json", """{"nodeType":"NodeType"}"""),
        ], target);

        Assert.False(
            File.Exists(Path.Combine(target, "Module", "Source", "ModuleContent.cs")),
            "withheld source must not survive on disk — a merged tree would keep compiling against it");
        // The directory it lived in goes too: an empty Source/ folder left behind reads as
        // "this package has no source" to a human and to a glob, which is a different claim.
        Assert.False(Directory.Exists(Path.Combine(target, "Module", "Source")));
        Assert.True(File.Exists(Path.Combine(target, "Module.json")));
    }

    [Fact]
    public void AFileDroppedByANewReleaseDoesNotSurvive()
    {
        // The same rule beyond source: a merged tree is neither the old release nor the new one,
        // so a build against it is reproducible from no commit at all.
        var target = Path.Combine(root, "Store");

        ModuleFetchCommand.Materialise(
            [Node("index.json", "{}"), Node("Retired.json", """{"nodeType":"NodeType"}""")], target);
        Assert.True(File.Exists(Path.Combine(target, "Retired.json")));

        ModuleFetchCommand.Materialise([Node("index.json", "{}")], target);

        Assert.False(File.Exists(Path.Combine(target, "Retired.json")));
    }

    [Fact]
    public void NestedPathsAreRecreatedAsATree()
    {
        var target = Path.Combine(root, "Publish");

        ModuleFetchCommand.Materialise(
            [Node("Slide/Source/SlideContent.cs", "public record SlideContent;")], target);

        Assert.Equal(
            "public record SlideContent;",
            File.ReadAllText(Path.Combine(target, "Slide", "Source", "SlideContent.cs")));
    }
}
