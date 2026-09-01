using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The guard over the test-config plumbing must live in the SAME file as the plumbing</b> —
/// <c>test/Directory.Build.props</c> — because that is the only file the fleet imports.
///
/// <para><b>What went wrong.</b> <c>test/Directory.Build.props</c> selects ONE of two
/// <c>Content</c> branches for <c>xunit.runner.json</c> and for <c>appsettings.json</c> (shared
/// default vs project-local override), and <c>VerifyXunitRunnerConfigCopied</c> — the target that
/// errors when the selection picks NEITHER — sat in the sibling
/// <c>test/Directory.Build.targets</c>. MSBuild auto-imports those two files independently, so a
/// consumer can take one without the other, and every satellite that consumes the plumbing does:
/// MeshWeaver.Plugins (78 <c>*.Test</c> projects) and MeshWeaver.SocialMedia (1) each import
/// <c>$(MeshWeaverRoot)/test/Directory.Build.props</c> explicitly from their own
/// <c>src/Directory.Build.props</c>, and <b>neither imports the targets file</b>. Measured
/// 2026-09-01: <b>zero of the five satellite checkouts</b> (.Plugins, .Education, .Reinsurance,
/// .SocialMedia, .Manufacturing) reference <c>test/Directory.Build.targets</c> at all — the other
/// three import neither file. The guard therefore covered this repository's ~15 test projects and
/// none of the 79 satellite ones.</para>
///
/// <para><b>Why it is a correctness problem, not tidiness.</b> When the two-branch selection picks
/// neither, the build still succeeds and the tests still run — under xUnit's OWN defaults:
/// <c>parallelizeTestCollections=true</c>, threads = core count, and <b>no <c>methodTimeout</c></b>.
/// The last of those is what ties this to the ambient-hang family (#2865). With the configured
/// 30 s <c>methodTimeout</c> in force, a wedged test is killed individually and <b>its transcript is
/// written</b> — the "strictly better witness" #2865 identifies. Without it nothing bounds the
/// test, the shard runs to its wall-clock cap, the host is killed (<c>exit=124</c> /
/// <c>HOST_CRASHED</c>) and the in-flight test's transcript is <b>destroyed</b>, which is precisely
/// the structural obstacle that issue records as the reason the family cannot be diagnosed from CI
/// artifacts at all. Whether a hang is investigable depends on this plumbing having worked, and
/// outside core nothing checked that it had.</para>
///
/// <para><b>Why a text guard.</b> The invariant is about which MSBuild file a target is declared in
/// — there is no assembly to reflect over, and the failure is silent by construction: the build
/// succeeds, the tests run, and only the settings they run under are wrong.</para>
/// </summary>
public class DirectoryBuildGuardTravelsWithItsSubjectTest
{
    private const string PropsPath = "test/Directory.Build.props";
    private const string TargetsPath = "test/Directory.Build.targets";
    private const string GuardTarget = "VerifyXunitRunnerConfigCopied";

    /// <summary>
    /// The guard target is declared in <c>test/Directory.Build.props</c> — the file every satellite
    /// imports — so importing the plumbing cannot leave the assertion behind.
    /// </summary>
    [Fact]
    public void TheGuardIsDeclaredInTheFileThatCarriesThePlumbing()
    {
        Assert.True(
            DeclaresGuardTarget(Read(PropsPath)),
            $"{PropsPath} no longer declares the <Target Name=\"{GuardTarget}\"> that proves its own "
            + "Content includes selected a branch. Every satellite repository imports THIS file and "
            + "only this file, so a guard declared anywhere else does not run for them — their test "
            + "assemblies would silently fall back to xUnit's defaults (unbounded parallelism, and "
            + "no methodTimeout, which is what turns a wedged test into a host kill that destroys "
            + "its own transcript — #2865). Put the guard back in this file; do not move it to "
            + $"{TargetsPath}.");
    }

    /// <summary>
    /// …and it must NOT drift back into <c>test/Directory.Build.targets</c>, which is the separable
    /// file no satellite imports. Declaring it there is the original defect, so this asserts the
    /// negative explicitly rather than trusting the positive above to imply it.
    /// </summary>
    [Fact]
    public void TheGuardIsNotDeclaredInTheFileNobodyImports()
    {
        Assert.False(
            DeclaresGuardTarget(Read(TargetsPath)),
            $"{TargetsPath} declares <Target Name=\"{GuardTarget}\">. MSBuild imports "
            + "Directory.Build.props and Directory.Build.targets independently, and no satellite "
            + "repository imports the targets file — so a guard living here validates core's test "
            + "projects and none of the fleet's. It belongs in the same file as the Content includes "
            + $"it checks ({PropsPath}).");
    }

    /// <summary>
    /// 🚨 Anti-vacuity. The two assertions above are about a guard over four <c>Content</c>
    /// includes; if those includes were renamed or removed, both would still pass while nothing was
    /// being guarded. This pins that the subject still exists in the same file — the failure this
    /// repository hits most often is a guard whose subject moved out from under it.
    ///
    /// <para>Matched against the file with XML comments STRIPPED. The props file documents its own
    /// plumbing and quotes these item shapes in prose, so a raw-text match would be satisfied by the
    /// comment describing an <c>ItemGroup</c> that had been deleted — an anti-vacuity check that is
    /// itself vacuous.</para>
    /// </summary>
    [Fact]
    public void ThePlumbingTheGuardChecksStillLivesInThatFile()
    {
        // 🚨 COMMENTS STRIPPED FIRST, and that is the whole point of this method. This file
        // *explains* its own plumbing at length, and that prose quotes the very item shapes matched
        // below — `<Content Include="appsettings.json">` appears verbatim inside a comment. Matching
        // the raw text would therefore keep passing after the real ItemGroup was deleted: the guard
        // would be satisfied by the documentation of the thing it is supposed to be guarding. (A
        // grep hit is not a binder — comments, embedded docs and resource strings all match.)
        var props = StripComments(Read(PropsPath));

        Assert.True(
            Regex.IsMatch(props, @"Content\s+Include=""[^""]*xunit\.runner\.json"""),
            $"{PropsPath} no longer contains a Content include for xunit.runner.json, so "
            + $"{GuardTarget} guards nothing. If the mechanism moved, the guard must move with it.");

        Assert.True(
            Regex.IsMatch(props, @"Content\s+Include=""[^""]*appsettings\.json"""),
            $"{PropsPath} no longer contains a Content include for appsettings.json, so the second "
            + $"half of {GuardTarget} guards nothing.");

        Assert.True(
            props.Contains("$(TargetDir)xunit.runner.json", StringComparison.Ordinal)
            && props.Contains("$(TargetDir)appsettings.json", StringComparison.Ordinal),
            $"{GuardTarget} in {PropsPath} no longer asserts that BOTH files reached $(TargetDir). "
            + "Each Exists() check is what makes a 'selected neither branch' regression red instead "
            + "of silent.");
    }

    /// <summary>
    /// Matches a <c>&lt;Target Name="VerifyXunitRunnerConfigCopied"</c> declaration, ignoring
    /// attribute order and whitespace, and deliberately NOT matching a mention inside a comment —
    /// both files reference the name in prose explaining where it lives and why.
    /// </summary>
    private static bool DeclaresGuardTarget(string xml) =>
        Regex.IsMatch(StripComments(xml), $@"<Target\s[^>]*Name\s*=\s*""{GuardTarget}""");

    private static string StripComments(string xml) =>
        Regex.Replace(xml, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate the repository root (no MeshWeaver.slnx above the test binary).");
    }
}
