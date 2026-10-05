#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Ladder row H1 — the 2026-10-05 incident, end to end, as far as core goes</b>
/// (<c>Doc/Architecture/ModuleUpdateLadder</c>).
///
/// <para><b>The incident.</b> The control instance ran store package <c>MeshWeaver.AI</c> 1.20.4.
/// Its image shipped a newer AI (1.21) that has <c>ThreadPreparation.Group</c>. A Hosting prebuilt
/// compiled against that newer AI recorded its AI dependency as <c>min:3.0.0.0</c>, was ADOPTED
/// against 1.20.4, and its first call threw <c>MissingMethodException: set_Group</c>: the PR
/// reviewer thread could not start.</para>
///
/// <para><b>What "the thread starts" is here.</b> The Hosting-shaped type's <c>OwningThread.Start()</c>
/// — the code that prepares the thread and sets <c>Group</c> — compiles against the module the boot
/// made effective and RUNS. Every step goes through the production rule that decides it: the store
/// copy is landed by the real <see cref="ModuleLandingService"/>; the image's copy carries the
/// <see cref="ImageModuleSeed"/> stamp in the closure lane's exact shape; the boot is
/// <see cref="ModuleActivationBoot"/>'s, resolved to a file by <see cref="ModuleActivationBoot.ResolveLoadPath"/>;
/// the prebuilt's record is written by the real NodeType compile (<see cref="NodeSetCompiler.Compile"/>)
/// and judged by <see cref="CompiledDependencies.Validate"/>.</para>
///
/// <para><b>The negative controls are in the suite.</b> The PRE-#6103 boot rule (no image stamp
/// consulted) picks the store's 1.20.4 — the defect, measured — and the PRE-#6116 resolver would
/// adopt the prebuilt against it, whose execution then throws exactly the incident's
/// <c>MissingMethodException</c>. Only after both are shown does the test take the right step.</para>
/// </summary>
public class ModuleUpdateLadderIncidentTest : IDisposable
{
    private const string PackagePath = "Plugins/AI";

    /// <summary>The identity every bundle of this epoch states — store copy included, which is why
    /// the identity cannot decide between the two copies.</summary>
    private const string PlatformCommit = "gafde4eabe0740ff658f3dae8a3073c33a2b3ea02";

    private const string SurfaceIdentity = "s4b2836629f0c58ad4ed0aa683190981a";
    private const string RunningPlatform = "3.0.0-ci.0";

    private readonly LadderModuleFixture fixture = new();
    private readonly ModuleLandingService landing;
    private readonly string imageModuleDir;

    public ModuleUpdateLadderIncidentTest()
    {
        landing = new ModuleLandingService(baseDirectory: Path.Combine(fixture.Root, "store"));
        imageModuleDir = Path.Combine(fixture.Root, "image", "modules", LadderModuleFixture.Module);
        Directory.CreateDirectory(imageModuleDir);
    }

    public void Dispose()
    {
        landing.Dispose();
        fixture.Dispose();
        GC.SuppressFinalize(this);
    }

    private string StoreRoot => Path.Combine(fixture.Root, "store");

    [Fact(Timeout = 180_000)]
    public async Task TheIncident_TheStoreCopyRunsAndTheThreadCannotStart_UntilTheImageRuleRuns_ThenItStarts()
    {
        var ct = TestContext.Current.CancellationToken;

        // ── The installation as it stood on 2026-10-05 ────────────────────────────────────────
        // The image's own AI 1.21 (has Group), stamped as the closure lane stamps it.
        var imageCopy = Path.Combine(imageModuleDir, LadderModuleFixture.Module + ".dll");
        File.Copy(fixture.EmitModule("image-build", LadderModuleFixture.Newer, withGroup: true), imageCopy);
        File.WriteAllText(Path.Combine(imageModuleDir, ImageModuleSeed.FileName),
            "{\n  \"schema\": \"mw-module-seed/1\",\n  \"module\": \"" + LadderModuleFixture.Module
            + "\",\n  \"package\": \"AI\",\n  \"version\": \"" + LadderModuleFixture.Newer
            + "\",\n  \"moduleVersion\": \"0123456789abcdef\"\n}\n");
        // The store's AI 1.20.4 (no Group), landed through the real landing.
        var storeBytes = File.ReadAllBytes(fixture.EmitModule("store-build", LadderModuleFixture.Older, withGroup: false));
        await landing.LandModule(
                LadderModuleFixture.Module, [(LadderModuleFixture.Module + ".dll", storeBytes)],
                frameworkMvid: PlatformCommit, packagePath: PackagePath, version: LadderModuleFixture.Older,
                minMeshVersion: null)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        // The Hosting prebuilt from the sealed set, compiled against the newer AI.
        var producer = fixture.LoadModule(fixture.EmitModule("producer", LadderModuleFixture.Newer, withGroup: true));
        var prebuilt = fixture.CompileHosting("prebuilt", LadderModuleFixture.UsesGroup, producer,
            LadderModuleFixture.ResolverOver(producer));

        // ── (a) The boot as it ran: the pre-#6103 rule, no image stamp consulted ─────────────
        var beforePath = EffectiveAiPath(BootWithoutTheImageStamp());
        beforePath.Should().StartWith(StoreRoot, "the defect, measured: the store's 1.20.4 overrides the image's 1.21");
        var beforeModule = fixture.LoadModule(beforePath);
        new InstalledModuleAssembly(beforeModule).PackageVersion.Should().Be(LadderModuleFixture.Older);

        // The incident's mechanism: the informational floor min:3.0.0.0 is met by 1.20.4…
        CompiledDependencies.Validate(
                CompiledDependencies.Compute([LadderModuleFixture.Module], LadderModuleFixture.InformationalOnlyResolverOver(producer), NodeTypeCompilationHelpers.ProcessToolchainId),
                LadderModuleFixture.InformationalOnlyResolverOver(beforeModule), NodeTypeCompilationHelpers.ProcessToolchainId)
            .IsSatisfied.Should().BeTrue("the pre-#6116 record cannot tell the two AI builds apart — that is how the prebuilt was adopted");
        // …and running the adopted prebuilt against it is exactly the incident.
        var thrown = Assert.Throws<TargetInvocationException>(() => fixture.RunStart(prebuilt.DllPath, beforePath));
        thrown.InnerException.Should().BeOfType<MissingMethodException>()
            .Which.Message.Should().Contain("set_Group");

        // With #6116 the same prebuilt is REFUSED against 1.20.4, naming both versions…
        var refused = CompiledDependencies.Validate(prebuilt.Dependencies,
            LadderModuleFixture.ResolverOver(beforeModule), NodeTypeCompilationHelpers.ProcessToolchainId);
        refused.Status.Should().Be(DependencyRecordStatus.FloorNotMet);
        refused.Problem.Should().Contain(LadderModuleFixture.Newer).And.Contain(LadderModuleFixture.Older);
        // …and the source compile against 1.20.4 fails NAMED — a diagnostic, never a MissingMethodException.
        var named = Assert.Throws<CompilationException>(() => fixture.CompileHosting("on-store-copy",
            LadderModuleFixture.UsesGroup, beforeModule, LadderModuleFixture.ResolverOver(beforeModule)));
        named.Message.Should().Contain("Group");

        // ── (b) The right step: a restart whose boot applies the image rule (#6103) ───────────
        var skips = new List<(string Module, string Reason)>();
        var after = Assert.Single(BootAgainstTheImage((m, r) => skips.Add((m, r))));
        after.Landed.Should().BeNull("a store copy OLDER than the image's own copy must not override it");
        Assert.Single(skips).Reason.Should().Contain(LadderModuleFixture.Older).And.Contain("OLDER than");
        var afterPath = ModuleActivationBoot.ResolveLoadPath(StoreRoot, after);
        afterPath.Should().Be(imageCopy, "the image's 1.21 is the effective AI");
        var afterModule = fixture.LoadModule(afterPath);

        CompiledDependencies.Validate(prebuilt.Dependencies, LadderModuleFixture.ResolverOver(afterModule),
                NodeTypeCompilationHelpers.ProcessToolchainId)
            .IsSatisfied.Should().BeTrue("against the effective 1.21 the prebuilt is adoptable");
        fixture.RunStart(prebuilt.DllPath, afterPath).Should().Be("PullRequests", "the adopted prebuilt starts the thread");
        var recompiled = fixture.CompileHosting("on-image-copy", LadderModuleFixture.UsesGroup, afterModule,
            LadderModuleFixture.ResolverOver(afterModule));
        fixture.RunStart(recompiled.DllPath, afterPath).Should().Be("PullRequests", "and so does a source compile");
    }

    private string EffectiveAiPath(IReadOnlyList<EffectiveModule> boot) =>
        ModuleActivationBoot.ResolveLoadPath(StoreRoot, Assert.Single(boot));

    /// <summary>The boot as #6103 found it: the identity rule only — the store copy states this
    /// platform's identity, so nothing declines it.</summary>
    private IReadOnlyList<EffectiveModule> BootWithoutTheImageStamp() =>
        ModuleActivationBoot.ComputeEffectiveModuleEntriesForPlatform(
            [Path.Combine(imageModuleDir, LadderModuleFixture.Module + ".dll")],
            ModuleActivationSidecar.Read(StoreRoot),
            floor => ModulePlatformFloor.DeclineReason(floor, RunningPlatform),
            entry => ModuleActivationBoot.LandedModuleDllExists(StoreRoot, entry),
            onSkipped: null,
            onAdvisory: null,
            [SurfaceIdentity, PlatformCommit]);

    /// <summary>The boot with the image's stamp read through the production reader.</summary>
    private IReadOnlyList<EffectiveModule> BootAgainstTheImage(Action<string, string> onSkipped) =>
        ModuleActivationBoot.ComputeEffectiveModuleEntriesAgainstImage(
            [Path.Combine(imageModuleDir, LadderModuleFixture.Module + ".dll")],
            ModuleActivationSidecar.Read(StoreRoot),
            floor => ModulePlatformFloor.DeclineReason(floor, RunningPlatform),
            entry => ModuleActivationBoot.LandedModuleDllExists(StoreRoot, entry),
            onSkipped,
            onAdvisory: null,
            [SurfaceIdentity, PlatformCommit],
            name => ImageModuleSeed.Read(imageModuleDir, name));
}
