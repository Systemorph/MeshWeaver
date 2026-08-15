using System.Linq;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the projection of a plugin manifest's caret dependency onto a NuGet range.
///
/// <para>A plugin declares <c>"requires": ["Store@^1.0.0"]</c> — the mesh's own manifest already
/// carries the dependency graph, so the nuspec is a projection of it rather than something
/// invented. The projection has to be right, because a range that is too WIDE never fails at pack
/// time: it fails when a resolver later picks a release the author declared incompatible.</para>
///
/// <para>🚨 <b>Below 1.0.0 the leading non-zero component is the breaking one.</b> An
/// implementation that always caps at the next MAJOR reads correctly against every dependency in
/// the tree today — all of them are <c>^1.0.0</c> — and silently widens the first 0.x module anyone
/// ships. That is why these cases are pinned now rather than when a 0.x plugin appears.</para>
/// </summary>
public class PluginManifestCaretRangeTest
{
    private static string? RangeFor(string requirement) =>
        new PluginManifest("X", "MeshWeaver.Plugin.X", "1.0.0", "d", null, [requirement])
            .ResolveDependencies()
            .Single()
            .Range;

    [Theory]
    // ≥ 1.0.0: the MAJOR is the breaking boundary.
    [InlineData("Store@^1.0.0", "[1.0.0,2.0.0)")]
    [InlineData("Store@^1.2.3", "[1.2.3,2.0.0)")]
    [InlineData("Store@^9.1.0", "[9.1.0,10.0.0)")]
    // 0.x: the MINOR is the breaking boundary — NOT 1.0.0.
    [InlineData("Store@^0.2.3", "[0.2.3,0.3.0)")]
    [InlineData("Store@^0.1.0", "[0.1.0,0.2.0)")]
    // 0.0.x: every PATCH may break, so the range admits exactly one version.
    [InlineData("Store@^0.0.3", "[0.0.3,0.0.4)")]
    public void CaretProjectsOntoTheBreakingBoundary(string requirement, string expected)
        => Assert.Equal(expected, RangeFor(requirement));

    [Fact]
    public void BareRequirementDeclaresNoBound()
        // `"requires": ["Store"]` — several plugins in the tree declare exactly this. It is the
        // author's statement that they do not constrain the version, and inventing a bound for
        // them would be the packer making a compatibility claim nobody made.
        => Assert.Null(RangeFor("Store"));

    [Fact]
    public void ExplicitRangePassesThroughUntouched()
        // Not every requirement is a caret. An author who wrote a NuGet interval means it.
        => Assert.Equal("[1.0.0,1.5.0)", RangeFor("Store@[1.0.0,1.5.0)"));

    [Fact]
    public void DependencyIdCarriesTheReservedPrefix()
    {
        // One prefix is what lets packageSourceMapping pin every plugin to the private feed with a
        // single rule; without it a typo'd id silently resolves against nuget.org.
        var (id, _) = new PluginManifest("X", "MeshWeaver.Plugin.X", "1.0.0", "d", null, ["Store@^1.0.0"])
            .ResolveDependencies()
            .Single();

        Assert.Equal("MeshWeaver.Plugin.Store", id);
    }
}
