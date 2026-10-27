#pragma warning disable CS1591

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// The publish-bake lane hands the tester repository files at <c>/repo/${X_FILE}</c>; under a
/// NARROWED bake <c>/repo</c> mounts only the staged packages, so every such file must be copied
/// into <c>$NARROW/$X_FILE</c> first — or the tester refuses the path it was given (exit 2) and
/// every narrowed publication of an adopting repository fails. The dynamic-edge baseline was added
/// without that copy (#7311 review), which is the class this guard holds shut: it reads the lane,
/// collects every <c>/repo/${…_FILE}</c> argument, and requires a matching copy into the narrowed
/// mount. The negative control runs the same check over a lane missing one copy.
/// </summary>
public class NarrowedBakeCarriesItsInputFilesGuard
{
    private const string PublishBake = ".github/workflows/node-repo-publish-bake.yml";

    /// <summary>The <c>/repo</c>-relative file variables a lane text passes to the tester that its
    /// narrowed staging does not copy into <c>$NARROW</c>.</summary>
    internal static IReadOnlyList<string> UncopiedRepoFiles(string text)
    {
        var passed = Regex.Matches(text, @"/repo/\$\{(?<var>[A-Z_]+_FILE)\}")
            .Select(m => m.Groups["var"].Value)
            .Distinct()
            .ToList();
        return passed
            .Where(v => !Regex.IsMatch(text,
                $@"cp\s+""\$GITHUB_WORKSPACE/\${v}""\s+""\$NARROW/\${v}"""))
            .OrderBy(v => v)
            .ToList();
    }

    [Fact]
    public void EveryRepoFileTheBakeIsHanded_IsCopiedIntoTheNarrowedMount()
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.Root, PublishBake));
        // The guard must actually see the files it protects — a pattern that matched nothing would
        // pass having checked nothing.
        var passed = Regex.Matches(text, @"/repo/\$\{(?<var>[A-Z_]+_FILE)\}").Select(m => m.Groups["var"].Value).ToHashSet();
        Assert.Contains("ALLOW_FILE", passed);
        Assert.Contains("WARNING_BASELINE_FILE", passed);
        Assert.Contains("DYNAMIC_BASELINE_FILE", passed);
        Assert.Empty(UncopiedRepoFiles(text));
    }

    [Fact]
    public void NegativeControl_ALaneMissingOneCopy_IsCaught()
    {
        const string lane = """
            cp "$GITHUB_WORKSPACE/$ALLOW_FILE" "$NARROW/$ALLOW_FILE"
            ALLOW_ARGS=(--allow "/repo/${ALLOW_FILE}")
            DYN_ARGS=(--dynamic-baseline "/repo/${DYNAMIC_BASELINE_FILE}")
            """;
        Assert.Equal(["DYNAMIC_BASELINE_FILE"], UncopiedRepoFiles(lane));
    }
}
