using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A registry keeps one shelf per platform LINE</b> (MeshWeaver.Plugins#5550, policy
/// <c>major-lines-run-separately</c>, Doc/Architecture/MajorLinesRunSeparately §4b).
///
/// <para><b>The defect.</b> One shelf held one head per module — the highest version — plus one
/// retained previous generation. Module versions say nothing about the line (line-3 AI is 1.52.0,
/// line-4 AI is 4.0.1), so a line-4 publish of 4.0.1 became the head, the line-3 build survived only
/// as the fallback, and the next line-4 publish (4.0.2) pushed it off entirely: a line-3 consumer of
/// that registry had nothing of its own line left. <see cref="TheSingleShelf_LosesTheLine3Build"/>
/// reproduces that on the real landing service; the tests after it prove <see cref="ModuleShelfLines"/>
/// keeps each line's head whatever the other line publishes.</para>
/// </summary>
public class ARegistryShelvesEachPlatformLineSeparatelyTest : IDisposable
{
    private const string Platform = "plat-a";
    private const string Module = "Acme.Ai";
    private const string Line3Identity = "c003e005";
    private const string Line4Identity = "c004e001";
    private const string Line4Floor = "4.0.13296";

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-shelfline-" + Guid.NewGuid().ToString("N"));
    private readonly ModuleLandingService own;
    private readonly ModuleShelfLines lines;

    public ARegistryShelvesEachPlatformLineSeparatelyTest()
    {
        Directory.CreateDirectory(root);
        // A line-3 registry on a configured (shared) root — memex.meshweaver.cloud's shape.
        own = new ModuleLandingService(null, root, null, platformIdentity: Platform);
        lines = new ModuleShelfLines(own, root, ownLine: 3,
            create: tree => new ModuleLandingService(null, tree, null, platformIdentity: Platform));
    }

    public void Dispose()
    {
        lines.Dispose();
        own.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is harmless */ }
        catch (UnauthorizedAccessException) { /* likewise */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The negative control — the defect's own shape on the ONE shelf every registry had: after a
    /// line-4 4.0.1 and 4.0.2 publish, no line-3 build is left as head or fallback.
    /// </summary>
    [Fact]
    public async Task TheSingleShelf_LosesTheLine3Build()
    {
        await Shelve(own, "1.52.0", Line3Identity, null);
        await Shelve(own, "4.0.1", Line4Identity, Line4Floor);
        await Shelve(own, "4.0.2", Line4Identity, Line4Floor);

        var entry = (await Activation(own)).Entries.Single(e => e.Name == Module);
        new[] { entry.Version, entry.PreviousVersion }.Should().NotContain("1.52.0",
            "this is the hazard: one shelf keeps the highest version and ONE fallback, so two line-4 "
            + "publishes push the last line-3 build off");
    }

    /// <summary>A line-4 publish of 4.0.1 and then 4.0.2 leaves the line-3 head 1.52.0 where it was.</summary>
    [Fact]
    public async Task Line4Publishes_LeaveTheLine3Head()
    {
        await Shelve(lines.ForUpload(Line3Identity, null), "1.52.0", Line3Identity, null);
        await Shelve(lines.ForUpload(Line4Identity, Line4Floor), "4.0.1", Line4Identity, Line4Floor);
        await Shelve(lines.ForUpload(Line4Identity, Line4Floor), "4.0.2", Line4Identity, Line4Floor);

        var line3 = (await Activation(lines.For(3))).Entries.Single(e => e.Name == Module);
        line3.Version.Should().Be("1.52.0", "no line-4 publish touches the line-3 shelf");
        line3.PreviousVersion.Should().BeNull("the line-3 shelf never held a line-4 build");

        var line4 = (await Activation(lines.For(4))).Entries.Single(e => e.Name == Module);
        line4.Version.Should().Be("4.0.2", "the line-4 head is the highest line-4 version");
        line4.PreviousVersion.Should().Be("4.0.1");

        lines.For(3).Should().BeSameAs(own, "the registry's own line is its own landing service, unchanged");
        lines.For(4).BaseDirectory.Should().Be(Path.Combine(root, ModuleRoot.LinesDirectoryName, "4"),
            "the line-4 shelf is the tree a line-4 replica lands into on the same volume");
    }

    /// <summary>The other direction: a line-3 publish leaves the line-4 head.</summary>
    [Fact]
    public async Task ALine3Publish_LeavesTheLine4Head()
    {
        await Shelve(lines.ForUpload(Line4Identity, Line4Floor), "4.0.1", Line4Identity, Line4Floor);
        await Shelve(lines.ForUpload(Line3Identity, null), "1.52.0", Line3Identity, null);
        await Shelve(lines.ForUpload(Line3Identity, null), "1.53.0", Line3Identity, null);

        (await Activation(lines.For(4))).Entries.Single(e => e.Name == Module).Version
            .Should().Be("4.0.1", "no line-3 publish touches the line-4 shelf");
        (await Activation(lines.For(3))).Entries.Single(e => e.Name == Module).Version
            .Should().Be("1.53.0");
    }

    /// <summary>An upload is routed by what it STATES: its identity first, then its floor, and one
    /// stating neither is the registry's own line, as every upload was before.</summary>
    [Fact]
    public void AnUpload_IsRoutedByWhatItStates()
    {
        lines.LineOfUpload(Line4Identity, null).Should().Be(4);
        lines.LineOfUpload("s0123456789abcdef", Line4Floor).Should().Be(4, "a legacy identity says nothing; the floor does");
        lines.LineOfUpload(Line3Identity, Line4Floor).Should().Be(3, "the identity is a fact about the bytes and wins");
        lines.LineOfUpload(null, null).Should().Be(3, "an upload stating nothing is the registry's own line");
        lines.ForUpload(null, null).Should().BeSameAs(own);
    }

    /// <summary>Where each line's shelf lives. Pure.</summary>
    [Fact]
    public void EachLine_HasItsOwnTree()
    {
        var shared = Path.Combine(root, "shared");
        var v4Tree = Path.Combine(shared, "lines", "4");
        ModuleShelfLines.TreeFor(shared, shared, 3, 3).Should().Be(shared);
        ModuleShelfLines.TreeFor(shared, shared, 3, 4).Should().Be(v4Tree);
        ModuleShelfLines.TreeFor(shared, v4Tree, 4, 3).Should().Be(shared, "a line-4 registry shelves line 3 into the legacy root");
        ModuleShelfLines.TreeFor(shared, v4Tree, 4, 2).Should().Be(shared, "every major up to 3 is one line");
        ModuleShelfLines.TreeFor(null, root, 3, 4).Should().Be(Path.Combine(root, "lines", "4"),
            "without a configured root the other line sits beside the own tree, never on it");
        ModuleShelfLines.TreeFor(null, root, 4, 3).Should().Be(Path.Combine(root, "lines", "3"));
    }

    /// <summary>
    /// A configured root that puts another line's shelf ON this process's own tree is refused, never
    /// relocated: a line-4 process whose own tree is the shared root itself (not <c>lines/4</c>) would
    /// otherwise shelve line 3 somewhere no line-3 replica reads.
    /// </summary>
    [Fact]
    public void AnotherLinesShelfOnTheOwnTree_IsRefused()
    {
        var shared = Path.Combine(root, "shared");
        Action act = () => ModuleShelfLines.TreeFor(shared, shared, 4, 3);
        act.Should().Throw<InvalidOperationException>().WithMessage("*two lines would share one shelf*");
    }

    /// <summary>Once the shelves are disposed, asking for another line's shelf is refused — the check
    /// and the creation sit in the same section Dispose drains under, so nothing is created after it.</summary>
    [Fact]
    public void AfterDispose_AnotherLinesShelf_IsRefused()
    {
        lines.For(4).Should().NotBeSameAs(own, "line 4 has a shelf of its own");
        lines.Dispose();
        Action act = () => lines.For(4);
        act.Should().Throw<ObjectDisposedException>();
        lines.For(3).Should().BeSameAs(own, "the own line is this process's own service, which this set does not own");
    }

    /// <summary>The line a registry CALLER is on: the stated <c>line</c>, else the line of the identity
    /// it asks in (what a caller predating the parameter sends), else unknown.</summary>
    [Theory]
    [InlineData("4", null, 4)]
    [InlineData("3", Line4Identity, 3)]
    [InlineData(null, Line4Identity, 4)]
    [InlineData(null, Line3Identity, 3)]
    [InlineData("2", null, 3)]
    [InlineData(" 4 ", null, 4)]
    [InlineData(null, "s0123456789abcdef", null)]
    [InlineData("four", null, null)]
    [InlineData(null, null, null)]
    public void TheCallerLine_IsTheStatedLineThenTheIdentity(string? line, string? identity, int? expected)
        => BundleLine.CallerLine(line, identity).Should().Be(expected);

    /// <summary>
    /// The running line with NO test seam — what production reads — is the line of the platform version
    /// this build was made for. It is read off the core assembly's AssemblyVersion, which the build pins
    /// to <c>&lt;major&gt;.&lt;minor&gt;.0.0</c> of that platform version (Directory.Build.props,
    /// <c>AssemblyVersion</c> from <c>_VersionNumeric</c>); the informational version carries the same
    /// platform version, so a drift between the two pins would surface here.
    /// </summary>
    [Fact]
    public void TheUnseamedRunningLine_IsTheLineOfTheBuildsPlatformVersion()
    {
        var informational = typeof(BundleLine).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var buildMajor = MeshWeaver.Hosting.PrebuiltAdoptionPolicy.MajorOf(informational);
        buildMajor.Should().NotBeNull($"the core assembly states its platform version ({informational ?? "none"})");

        BundleLine.RunningLine(null).Should().Be(buildMajor is { } major ? BundleLine.LineOf(major) : null,
            "every client splits the index on this line, so it must be the line of the platform it runs");
    }

    /// <summary>The floor's line rule and the bundle's line rule share one legacy line.</summary>
    [Fact]
    public void TheFloorAndTheBundleShareOneLegacyLine()
    {
        PlatformFloor.LegacyLine.Should().Be(BundleLine.LegacyLine);
        PlatformFloor.LegacyLine.Should().Be(ModuleRoot.LastSharedRootMajor);
    }

    // ───────────────────────────────────────────────────────────────────────── harness

    private static byte[] Bytes =>
        File.ReadAllBytes(typeof(BundleReader).Assembly.Location);

    private static Task<ModuleActivationList> Activation(ModuleLandingService shelf) =>
        shelf.GetActivation().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    private static Task<ModuleLandingOutcome> Shelve(
        ModuleLandingService shelf, string version, string identity, string? floor) =>
        shelf.ShelveModule(Module, [(Module + ".dll", Bytes)], frameworkMvid: identity,
                packagePath: "Plugins/AI", version: version, minMeshVersion: floor,
                // A distinct asset per version so each landing is its own content-addressed generation.
                staticAssets: [("wwwroot/version.txt", Encoding.UTF8.GetBytes(version))])
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
}
