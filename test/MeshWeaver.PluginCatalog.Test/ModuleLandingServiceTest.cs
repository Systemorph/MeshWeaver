#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The runtime <c>modules/</c> writer (#1664 Slice A, step 7): a landed module's files + its
/// activation entry, the platform-FLOOR gate at placement (the ONE
/// <see cref="ModulePlatformFloor"/> notion every module call site shares — the built-against
/// MVID is recorded as diagnostics, never refused), the app-closure same-identity refusal, and
/// uninstall (disable + delete, restart-as-activation).
/// </summary>
public class ModuleLandingServiceTest : IDisposable
{
    private readonly string baseDirectory =
        Path.Combine(Path.GetTempPath(), "mw-landing-" + Guid.NewGuid().ToString("N"));

    /// <summary>The RUNNING framework identity — exactly what
    /// <c>NodeTypeCompilationHelpers.FrameworkVersion</c> resolves (the stamped commit identity
    /// on a CI-built Graph, the MVID locally — #1660 WS3), read through the ONE public reading so
    /// this test can never diverge from what the gate compares. Recorded on landings as
    /// DIAGNOSTIC metadata.</summary>
    private static string LiveFrameworkMvid => PrebuiltAssemblySeeder.LiveFrameworkMvid;

    private ModuleLandingService Service => new(baseDirectory: baseDirectory);

    public ModuleLandingServiceTest() => Directory.CreateDirectory(baseDirectory);

    public void Dispose()
    {
        if (Directory.Exists(baseDirectory))
            Directory.Delete(baseDirectory, recursive: true);
    }

    [Fact]
    public async Task LandModule_HappyPath_LandsFilesAndActivationEntry_AndFlagsPendingRestart()
    {
        using var service = Service;
        await service.LandModule(
                "Acme.Widgets",
                [("Acme.Widgets.dll", [1, 2, 3]), ("Acme.Widgets.Support.dll", [4, 5])],
                LiveFrameworkMvid,
                packagePath: "Plugins/acme-widgets",
                version: "1.2.0",
                minMeshVersion: "0.0.1")
            .FirstAsync().ToTask();

        // The activation entry + the minimal step-10 restart-required signal.
        var list = ModuleActivationSidecar.Read(baseDirectory);
        var entry = list.Entries.Should().ContainSingle().Subject;

        // The files land in a fresh GENERATION directory the entry points at — resolved by the
        // one rule boot and the serving side share.
        entry.Directory.Should().StartWith("Acme.Widgets@",
            "a landing writes modules/<name>@<id>/ and moves the pointer — it never overwrites");
        var dir = ModuleLandingService.ModuleDirectoryFor(baseDirectory, "Acme.Widgets", entry);
        File.ReadAllBytes(Path.Combine(dir, "Acme.Widgets.dll")).Should().Equal([1, 2, 3]);
        File.Exists(Path.Combine(dir, "Acme.Widgets.Support.dll")).Should().BeTrue();
        // No staging leftovers.
        Directory.GetDirectories(Path.Combine(baseDirectory, "modules"))
            .Select(Path.GetFileName)
            .Should().NotContain(n => n!.StartsWith(".staging-"));

        entry.Name.Should().Be("Acme.Widgets");
        entry.Source.Should().Be(ModuleActivationSources.Store);
        entry.PackagePath.Should().Be("Plugins/acme-widgets");
        entry.FrameworkMvid.Should().Be(LiveFrameworkMvid, "the built-against MVID is recorded as diagnostics");
        entry.Version.Should().Be("1.2.0");
        entry.MinMeshVersion.Should().Be("0.0.1", "the floor is what boot and the serve side re-check");
        entry.Enabled.Should().BeTrue();
        list.PendingRestart.Should().BeTrue("landing takes effect only at the next restart");
    }

    [Fact]
    public async Task LandModule_ReLanding_ReplacesFolderAndEntry_NeverDuplicates()
    {
        using var service = Service;
        await service.LandModule("Acme.Widgets", [("Acme.Widgets.dll", [1])], LiveFrameworkMvid)
            .FirstAsync().ToTask();
        await service.LandModule("Acme.Widgets", [("Acme.Widgets.dll", [9, 9])], LiveFrameworkMvid)
            .FirstAsync().ToTask();

        var entry = ModuleActivationSidecar.Read(baseDirectory).Entries
            .Should().ContainSingle("re-landing upserts the entry, never appends a duplicate")
            .Subject;
        var dir = ModuleLandingService.ModuleDirectoryFor(baseDirectory, "Acme.Widgets", entry);
        File.ReadAllBytes(Path.Combine(dir, "Acme.Widgets.dll"))
            .Should().Equal([9, 9], "the pointer moves to the fresh generation");
        Directory.GetDirectories(Path.Combine(baseDirectory, "modules"))
            .Should().HaveCount(2,
                "the superseded generation SURVIVES the landing path — a running pod may hold "
                + "it open; boot GC reclaims it once nothing references it");
    }

    [Fact]
    public async Task LandModule_PlatformBelowDeclaredFloor_Refuses_NamingBothVersions()
    {
        using var service = Service;

        var act = () => service
            .LandModule("Acme.Widgets", [("Acme.Widgets.dll", [1])],
                LiveFrameworkMvid, minMeshVersion: "999.0.0")
            .FirstAsync().ToTask();

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "landing bytes whose required API surface does not exist here would surface only "
                + "as a MissingMethodException at the next boot"))
            .Which.Message.Should().Contain("999.0.0").And.Contain(
                // Non-null on any stamped build — ModulePlatformFloorTest pins that.
                ModulePlatformFloor.RunningVersion!,
                "the refusal must name BOTH versions so the operator can see which side is behind");

        var modulesRoot = Path.Combine(baseDirectory, "modules");
        (Directory.Exists(modulesRoot) ? Directory.GetDirectories(modulesRoot) : [])
            .Should().BeEmpty("declined bytes never reach disk — no generation, no staging");
        ModuleActivationSidecar.Read(baseDirectory).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task LandModule_ForeignBuiltAgainstMvid_Lands_TheMvidIsDiagnosticOnly()
    {
        // 🚨 The design decision of the module lane: modules bind by SIMPLE NAME and their
        // contract is API compatibility (the minMeshVersion floor) — a bundle produced by a
        // different platform build lands fine. MVID equality is bake semantics and stays with the
        // NodeType assembly lane; gating modules on it would forbid the ex-post Store install
        // across platform versions the lane exists for.
        using var service = Service;
        var foreignBuild = Guid.NewGuid().ToString("N");

        await service.LandModule(
                "Acme.Widgets", [("Acme.Widgets.dll", [1])], foreignBuild, version: "1.0.0")
            .FirstAsync().ToTask();

        var entry = ModuleActivationSidecar.Read(baseDirectory).Entries.Should().ContainSingle()
            .Subject;
        File.Exists(Path.Combine(
                ModuleLandingService.ModuleDirectoryFor(baseDirectory, "Acme.Widgets", entry),
                "Acme.Widgets.dll"))
            .Should().BeTrue();
        entry.FrameworkMvid.Should().Be(foreignBuild,
            "the built-against MVID is kept as diagnostics naming the exact producing build");
    }

    [Fact]
    public async Task LandModule_AppClosureNameCollision_Refuses()
    {
        // The same-identity trap-door: modules/<name>/<name>.dll WINS over the app folder in
        // ResolveModulePath, so a module named after an app-closure assembly would shadow the
        // platform's own binary at the next boot.
        File.WriteAllBytes(Path.Combine(baseDirectory, "Acme.Platform.dll"), [1]);

        using var service = Service;
        var act = () => service
            .LandModule("Acme.Platform", [("Acme.Platform.dll", [2])], LiveFrameworkMvid)
            .FirstAsync().ToTask();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Acme.Platform.dll");
        Directory.Exists(Path.Combine(baseDirectory, "modules", "Acme.Platform")).Should().BeFalse();
    }

    [Fact]
    public async Task LandModule_WithoutItsEntryDll_Refuses()
    {
        using var service = Service;
        var act = () => service
            .LandModule("Acme.Widgets", [("Acme.Widgets.Support.dll", [1])], LiveFrameworkMvid)
            .FirstAsync().ToTask();
        await act.Should().ThrowAsync<ArgumentException>(
            "a modules/<name>/ folder without <name>.dll could never load");
    }

    [Fact]
    public async Task RemoveModule_DisablesEntry_DeletesFolder_FlagsPendingRestart()
    {
        using var service = Service;
        await service.LandModule("Acme.Widgets", [("Acme.Widgets.dll", [1])], LiveFrameworkMvid)
            .FirstAsync().ToTask();
        // Simulate the boot that consumed the landing's restart flag, so the assert below
        // proves REMOVAL re-raises it.
        ModuleActivationSidecar.Write(baseDirectory,
            ModuleActivationSidecar.Read(baseDirectory) with { PendingRestart = false });

        await service.RemoveModule("Acme.Widgets").FirstAsync().ToTask();

        Directory.GetDirectories(Path.Combine(baseDirectory, "modules"))
            .Should().BeEmpty("uninstall deletes the landed generation (best-effort here; on a "
                + "shared volume a loaded copy survives to boot GC)");
        var list = ModuleActivationSidecar.Read(baseDirectory);
        var entry = list.Entries.Should().ContainSingle().Subject;
        entry.Enabled.Should().BeFalse("the entry is kept, disabled — history + idempotence");
        entry.Directory.Should().BeNull(
            "the pointer is cleared so boot GC can reclaim the generation — a disabled entry "
            + "must not keep its directory referenced");
        list.PendingRestart.Should().BeTrue("the removal takes effect at the next restart");
    }

    [Fact]
    public async Task RemoveModule_UnknownName_Refuses()
    {
        using var service = Service;
        var act = () => service.RemoveModule("Never.Landed").FirstAsync().ToTask();
        await act.Should().ThrowAsync<InvalidOperationException>(
            "publish-laid-out module folders are the deployment's, not uninstall's");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("..")]
    public async Task LandModule_InvalidModuleName_Refuses(string name)
    {
        using var service = Service;
        var act = () => service
            .LandModule(name, [(name + ".dll", [1])], LiveFrameworkMvid)
            .FirstAsync().ToTask();
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
