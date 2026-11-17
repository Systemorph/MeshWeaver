using System;
using System.IO;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A package that has not opted into a later platform line must not block that line's module set
/// (policy <c>major-lines-run-separately</c>).
///
/// <para><b>The incident.</b> A plugin registry rolled from line 3 to line 4. Its first line-4 replica
/// proposed module set 1 with no module in it (its line-4 tree was still empty), and every line-4
/// replica after it reported dozens of modules landed in that tree but "in NO proposed module set": AI,
/// the chat, the view packs, every required module. The registry also carried courses that have not
/// opted into line 4: their install records state no platform floor and require
/// <c>AI@^1.0.0</c> (line-3 numbering). The proposal judged those records on every line, compared
/// <c>AI@^1.0.0</c> with the line-4 tree's AI 4.0.1 — a build whose framework identity says line 4 —
/// and refused every line-4 proposal ("[ModuleSet] NOT proposing the landed module set"). Nothing in
/// the line-4 set was inconsistent; one course that predates the split held the whole line back.</para>
///
/// <para><b>The rule pinned here.</b> A record that states no floor predates the split, so its
/// requirements speak about legacy-line builds only (<see cref="BundleLine.Read(string?, string?, string?)"/>
/// reads a build that states nothing the same way): it is not compared with a dependency build that
/// states a later line, and it still is with one of the legacy line. A record that states the tree's
/// line keeps being judged against that line's builds.</para>
///
/// <para>Real seams: the real <see cref="ModuleLandingService"/> lands real assembly bytes into a
/// throwaway module root and proposes the set through <see cref="ModuleLandingService.ProposeCheckedModuleSet"/>,
/// the call every landing wave closes with.</para>
/// </summary>
public class AFloorlessRecordDoesNotBlockALaterLinesModuleSetTest : IDisposable
{
    private const string AiModule = "MeshWeaver.AI";
    private const string Line4Identity = "c004e001";
    private const string Line4Floor = "4.0.13296";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-floorless-line-" + Guid.NewGuid().ToString("N")[..8]);

    public AFloorlessRecordDoesNotBlockALaterLinesModuleSetTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the test is harmless */ }
        catch (UnauthorizedAccessException) { /* likewise */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>The course: content only, no floor, a line-3 requirement on AI.</summary>
    private static PackageManifest Course(string? floor = null) => new()
    {
        Id = "AgenticBusiness",
        Name = "AgenticBusiness",
        Kind = PackageKind.NodeRepo,
        TargetPartition = "AgenticBusiness",
        SourceFolder = "AgenticBusiness",
        Version = "1.5.0",
        ReleasedVersion = "1.5.0",
        MinMeshVersion = floor,
        Requires = ["AI@^1.0.0", "Edu@^1.0.0", "Store@^1.0.0"],
    };

    private static PackageManifest Ai => new()
    {
        Id = "AI",
        Name = "AI",
        Kind = PackageKind.NodeRepo,
        TargetPartition = "AI",
        SourceFolder = "AI",
        Version = "4.0.1",
        ReleasedVersion = "4.0.1",
        Module = AiModule,
        MinMeshVersion = Line4Floor,
    };

    private static ModuleActivationList AiLanded(string version, string? identity, string? floor) => new()
    {
        Entries =
        [
            new ModuleActivationEntry
            {
                Name = AiModule, Version = version, PackagePath = "Plugins/AI", Enabled = true,
                Directory = AiModule + "@g", FrameworkMvid = identity, MinMeshVersion = floor,
            },
        ],
    };

    /// <summary>THE reproduction, through the proposal itself: the line-4 tree holds AI 4.0.1 (identity
    /// <c>c004e001</c>), the install records hold the line-4 AI record and a course stating no floor that
    /// requires <c>AI@^1.0.0</c>. The set is proposed and names AI. Before the fix the proposal faulted
    /// with "Not proposing a module set: 1 declared dependency floor(s) are not met".</summary>
    [Fact]
    public async Task AFloorlessCourse_DoesNotRefuse_TheLine4SetThatLoadsAi4()
    {
        // The proposal judges the records of the line this source tree builds, so the incident's shape
        // needs a line past the shared root — the same precondition ModuleTreePerPlatformLineTest pins.
        (ModuleRoot.RunningMajor > ModuleRoot.LastSharedRootMajor).Should().BeTrue(
            "this source tree is a platform line past the legacy line; on the legacy line the course IS on the tree's line");
        using var landing = new ModuleLandingService(baseDirectory: root);
        await landing.LandModule(
                AiModule,
                [(AiModule + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
                frameworkMvid: Line4Identity,
                packagePath: "Plugins/AI",
                version: "4.0.1",
                minMeshVersion: Line4Floor,
                staticAssets: [("wwwroot/version.txt", Encoding.UTF8.GetBytes("4.0.1"))])
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        var proposed = await landing.ProposeCheckedModuleSet([Ai, Course()])
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        proposed.Should().NotBeNull("a course that has not opted into line 4 says nothing about line 4's AI build");
        proposed!.Generations.Keys.Should().Contain(AiModule, "the landed module is in the set every replica adopts");
    }

    [Fact]
    public void AFloorlessRecord_IsNotComparedWithABuildThatStatesALaterLine()
    {
        ModuleDependencyFloor.UnmetOnLine([Course()], AiLanded("4.0.1", Line4Identity, Line4Floor), line: 4)
            .Should().BeEmpty("AI 4.0.1 states line 4 by its framework identity, the course states no line");
        ModuleDependencyFloor.UnmetOnLine([Course()], AiLanded("4.0.1", identity: null, Line4Floor), line: 4)
            .Should().BeEmpty("a build's declared floor states its line too, when its identity is not a compatibility key");
    }

    [Fact]
    public void AFloorlessRecord_IsStillJudgedAgainstALegacyLineBuild()
    {
        // Control: on the legacy line the course's requirement is meaningful and still refuses a set
        // that loads an AI below it.
        ModuleDependencyFloor.UnmetOnLine([Course()], AiLanded("0.9.0", "c003e001", "3.1.12509"), line: 3)
            .Should().ContainSingle("AI 0.9.0 is a legacy-line build below the course's AI@^1.0.0");
        ModuleDependencyFloor.UnmetOnLine([Course()], AiLanded("0.9.0", identity: null, floor: null), line: 4)
            .Should().ContainSingle("a build that states no line is a legacy-line build, so the check is kept");
    }

    [Fact]
    public void ARecordOnTheTreesLine_IsStillJudgedAgainstThatLinesBuild()
    {
        // Control: the fix does not weaken the check for packages that opted in.
        ModuleDependencyFloor.UnmetOnLine([Course(Line4Floor)], AiLanded("4.0.1", Line4Identity, Line4Floor), line: 4)
            .Should().ContainSingle("a line-4 record requiring AI@^1.0.0 is inconsistent with the line-4 AI 4.0.1");
    }
}
