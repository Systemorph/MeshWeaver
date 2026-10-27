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

    private const string Gate = ".github/workflows/node-repo-gate.yml";

    /// <summary>
    /// The gate lane's narrowed inputs that are NOT staged at their repository-relative path: every
    /// <c>$P_REPO/$X_FILE</c> argument must be staged by <c>stage_input "$X_FILE"</c>, the stager
    /// must copy to <c>$REPO_MOUNT/$1</c> (the same relative path), and no input may be staged or
    /// named by <c>basename</c> — two inputs sharing a file name in different directories would
    /// land on one path and the second copy would overwrite the first.
    /// </summary>
    internal static IReadOnlyList<string> GateInputsNotStagedByPath(string text)
    {
        var problems = new List<string>();
        var passed = Regex.Matches(text, @"""\$P_REPO/\$(?<var>[A-Z_]+_FILE)""")
            .Select(m => m.Groups["var"].Value).Distinct().ToList();
        problems.AddRange(passed
            .Where(v => !Regex.IsMatch(text, $@"stage_input\s+""\${v}"""))
            .Select(v => $"{v}: not staged by stage_input"));
        if (passed.Count > 0 && !text.Contains(@"cp ""$GITHUB_WORKSPACE/$1"" ""$REPO_MOUNT/$1""", System.StringComparison.Ordinal))
            problems.Add("stage_input does not copy to the repository-relative path");
        problems.AddRange(Regex.Matches(text, @"basename\s+""\$(?<var>[A-Z_]+_FILE)""")
            .Select(m => $"{m.Groups["var"].Value}: staged or named by basename"));
        return problems.Distinct().OrderBy(p => p).ToList();
    }

    [Fact]
    public void EveryGateInput_IsStagedAtItsRepositoryRelativePath()
    {
        var text = File.ReadAllText(Path.Combine(RepoPaths.Root, Gate));
        var passed = Regex.Matches(text, @"""\$P_REPO/\$(?<var>[A-Z_]+_FILE)""").Select(m => m.Groups["var"].Value).ToHashSet();
        Assert.Contains("ALLOW_FILE", passed);
        Assert.Contains("WARNING_BASELINE_FILE", passed);
        Assert.Contains("DYNAMIC_BASELINE_FILE", passed);
        Assert.Empty(GateInputsNotStagedByPath(text));
    }

    /// <summary>Negative control: the pre-fix gate shape — copies by basename, so
    /// <c>warnings/debt.allow</c> and <c>dynamic/debt.allow</c> collide — is caught.</summary>
    [Fact]
    public void NegativeControl_GateInputsStagedByBasename_AreCaught()
    {
        const string lane = """
            cp "$GITHUB_WORKSPACE/$WARNING_BASELINE_FILE" "$REPO_MOUNT/"
            WARN_ARGS=(--warning-baseline "$P_REPO/$(basename "$WARNING_BASELINE_FILE")")
            cp "$GITHUB_WORKSPACE/$DYNAMIC_BASELINE_FILE" "$REPO_MOUNT/"
            DYN_ARGS=(--dynamic-baseline "$P_REPO/$(basename "$DYNAMIC_BASELINE_FILE")")
            """;
        Assert.Equal(
            ["DYNAMIC_BASELINE_FILE: staged or named by basename", "WARNING_BASELINE_FILE: staged or named by basename"],
            GateInputsNotStagedByPath(lane));
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
