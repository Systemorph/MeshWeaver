using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#6067 — the pure and file-level halves of "a module's bytes are never labelled
/// with a version they are not, and a set whose declared dependency floor is not met is never
/// proposed". The measured shape, 2026-10-04: the registry advertised <c>AI 1.20.1</c> for the
/// 1.19.4 bytes, consumers landed them stamped 1.20.1, and Hosting (<c>requires AI@^1.20.0</c>)
/// passed every check and threw <c>MissingMethodException</c> at run time.
/// </summary>
public sealed class ModuleVersionLabelTruthTest : IDisposable
{
    private const string Ai = "MeshWeaver.AI";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-labeltruth-" + Guid.NewGuid().ToString("N"));

    private readonly ModuleLandingService landing;

    public ModuleVersionLabelTruthTest()
    {
        Directory.CreateDirectory(root);
        landing = new ModuleLandingService(baseDirectory: root);
    }

    public void Dispose()
    {
        landing.Dispose();
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    // ─────────────────────────────── the publish side: the shelf is labelled by the bundle

    /// <summary>An upload whose <c>?version=</c> disagrees with the version the bundle declares is
    /// refused, naming both — before #6067 the query won and the bytes were shelved under it.</summary>
    [Fact]
    public void Publish_RefusesAnUploadLabelledWithAVersionItsBundleDoesNotDeclare()
    {
        var (accepted, decline) = ModulePublish.Validate(
            "AI", PublishManifest("1.19.4"), [new BundleReader.ModuleFile(Ai + ".dll", [1])],
            version: "1.20.1");

        Assert.Null(accepted);
        Assert.NotNull(decline);
        Assert.Contains("1.20.1", decline);
        Assert.Contains("1.19.4", decline);
    }

    /// <summary>The control: the pack lane's real shape — query and manifest agree — shelves at
    /// the declared version, and an upload with no query takes the manifest's.</summary>
    [Fact]
    public void Publish_ShelvesAtTheDeclaredVersion_WhenQueryAndBundleAgree()
    {
        var files = new[] { new BundleReader.ModuleFile(Ai + ".dll", [1]) };
        var (agreeing, _) = ModulePublish.Validate("AI", PublishManifest("1.20.1"), files, version: "1.20.1");
        var (unstated, _) = ModulePublish.Validate("AI", PublishManifest("1.20.1"), files);

        Assert.Equal("1.20.1", agreeing!.Version);
        Assert.Equal("1.20.1", unstated!.Version);
    }

    // ─────────────────────────────── the consumer side: the landing compares the label

    /// <summary>The registry-served shape: the archive's top-level version is the URL's, the
    /// module section states the version its bytes were shelved at. A disagreement is a mislabel.</summary>
    [Fact]
    public void Landing_NamesAMislabel_WhenTheModuleSectionDeclaresAnotherVersion()
    {
        var served = new BundleReader.Manifest("AI", "1.20.1", "s-id", [],
            new BundleReader.ModuleRef(Ai, [Ai + ".dll"]) { Version = "1.19.4" });

        var mislabel = PluginBundleClient.VersionMislabel("1.20.1", served);

        Assert.NotNull(mislabel);
        Assert.Contains("1.20.1", mislabel);
        Assert.Contains("1.19.4", mislabel);
        Assert.Equal("1.19.4", PluginBundleClient.DeclaredModuleVersion(served));
    }

    /// <summary>A producer's bundle states its version only at the top level — compared there.</summary>
    [Fact]
    public void Landing_NamesAMislabel_WhenTheManifestVersionDisagrees()
    {
        var packed = new BundleReader.Manifest("AI", "1.19.4", "s-id", [],
            new BundleReader.ModuleRef(Ai, [Ai + ".dll"]));

        Assert.NotNull(PluginBundleClient.VersionMislabel("1.20.1", packed));
        Assert.Null(PluginBundleClient.VersionMislabel("1.19.4", packed));
    }

    /// <summary>A bundle that states no version cannot disagree — it lands as before.</summary>
    [Fact]
    public void Landing_HasNothingToCompare_WhenTheBundleStatesNoVersion()
    {
        var unversioned = new BundleReader.Manifest("AI", null, "s-id", [],
            new BundleReader.ModuleRef(Ai, [Ai + ".dll"]));

        Assert.Null(PluginBundleClient.VersionMislabel("1.20.1", unversioned));
        Assert.Null(PluginBundleClient.DeclaredModuleVersion(unversioned));
    }

    // ─────────────────────────────── the range rule

    [Theory]
    [InlineData("^1.20.0", "1.20.1", true)]
    [InlineData("^1.20.0", "1.20.0", true)]
    [InlineData("^1.20.0", "1.21.3", true)]
    [InlineData("^1.20.0", "1.19.4", false)]
    [InlineData("^1.20.0", "2.0.0", false)]
    [InlineData("^0.3.0", "0.3.9", true)]
    [InlineData("^0.3.0", "0.4.0", false)]
    [InlineData("~1.20.0", "1.20.7", true)]
    [InlineData("~1.20.0", "1.21.0", false)]
    [InlineData(">=1.20.0", "3.0.0", true)]
    [InlineData(">=1.20.0", "1.19.9", false)]
    [InlineData("1.20.1", "1.20.1", true)]
    [InlineData("1.20.1", "1.20.2", false)]
    [InlineData("", "0.0.1", true)]
    public void Satisfies_ReadsTheDeclaredRange(string range, string version, bool expected) =>
        Assert.Equal(expected, ModuleDependencyFloor.Satisfies(range, version));

    /// <summary>A shape the rule does not understand is UNVERIFIABLE — never met, never refused.</summary>
    [Theory]
    [InlineData("1.x")]
    [InlineData("^latest")]
    [InlineData("<2.0.0 || >=3")]
    public void Satisfies_IsNull_ForARangeItCannotRead(string range) =>
        Assert.Null(ModuleDependencyFloor.Satisfies(range, "1.20.1"));

    // ─────────────────────────────── the set: a declared floor must be met by what loads

    /// <summary>
    /// 🚨 THE repro, on the set: Hosting requires <c>AI@^1.20.0</c> and the landed AI module is
    /// 1.19.4. The unmet requirement is named with both packages and both versions.
    /// </summary>
    [Fact]
    public async Task Unmet_NamesTheDependentTheDependencyAndBothVersions()
    {
        await LandAi("1.19.4");

        var unmet = Assert.Single(ModuleDependencyFloor.Unmet(Installed(), ModuleActivationSidecar.Read(root)));

        Assert.Equal("Hosting", unmet.Dependent);
        Assert.Equal("AI@^1.20.0", unmet.Requirement);
        Assert.Equal(Ai, unmet.Module);
        Assert.Equal("1.19.4", unmet.LoadedVersion);
        var sentence = unmet.Describe();
        Assert.Contains("Hosting", sentence);
        Assert.Contains("1.55.3", sentence);
        Assert.Contains("AI@^1.20.0", sentence);
        Assert.Contains("1.19.4", sentence);
    }

    /// <summary>The control: the same set with AI 1.20.1 landed has nothing unmet.</summary>
    [Fact]
    public async Task Unmet_IsEmpty_WhenTheLandedDependencySatisfiesTheFloor()
    {
        await LandAi("1.20.1");

        Assert.Empty(ModuleDependencyFloor.Unmet(Installed(), ModuleActivationSidecar.Read(root)));
    }

    /// <summary>A requirement whose dependency lands no module here is not this check's to judge.</summary>
    [Fact]
    public async Task Unmet_IgnoresARequirementWithNoLandedModule()
    {
        await LandAi("1.19.4");
        var installed = ImmutableList.Create(
            new PackageManifest { Id = "Hosting", Requires = ["Store@^1.0.0"] });

        Assert.Empty(ModuleDependencyFloor.Unmet(installed, ModuleActivationSidecar.Read(root)));
    }

    /// <summary>
    /// 🚨 A wave that would propose a set violating a declared floor proposes NOTHING: the call
    /// faults naming the floor, and no set record is written — the mesh stays on the set it runs.
    /// </summary>
    [Fact]
    public async Task CheckedProposal_RefusesASetWhoseFloorIsNotMet_AndWritesNoSet()
    {
        await LandAi("1.19.4");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await landing.ProposeCheckedModuleSet(Installed())
                .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken));

        Assert.Contains("AI@^1.20.0", refusal.Message);
        Assert.Contains("1.19.4", refusal.Message);
        Assert.Null(ModuleSetStore.Read(root).Proposed);
    }

    /// <summary>The control: the satisfying set is proposed exactly as the unchecked call would.</summary>
    [Fact]
    public async Task CheckedProposal_ProposesASetWhoseFloorsAreMet()
    {
        await LandAi("1.20.1");

        var proposed = await landing.ProposeCheckedModuleSet(Installed())
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

        Assert.NotNull(proposed);
        Assert.Contains(Ai, proposed!.Generations.Keys);
    }

    private static ImmutableList<PackageManifest> Installed() =>
    [
        new PackageManifest
        {
            Id = "Hosting", ReleasedVersion = "1.55.3", Module = "MeshWeaver.SelfUpdate.Aks",
            Requires = ["Store@^1.0.0", "AI@^1.20.0"],
        },
        new PackageManifest { Id = "AI", ReleasedVersion = "1.20.1", Module = Ai },
    ];

    private async Task LandAi(string version) =>
        await landing.LandModule(
                Ai,
                [(Ai + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
                packagePath: "Plugins/AI",
                version: version)
            .Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);

    private static BundleReader.Manifest PublishManifest(string version) =>
        new("AI", version, "test-build", [], new BundleReader.ModuleRef(Ai, [Ai + ".dll"]));
}
