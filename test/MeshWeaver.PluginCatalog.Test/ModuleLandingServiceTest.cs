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
/// activation entry, the MVID gate at placement (the SAME
/// <see cref="PrebuiltAssemblySeeder.DeclineReason"/> identity every other prebuilt lane gates
/// on), the app-closure same-identity refusal, and uninstall (disable + delete,
/// restart-as-activation).
/// </summary>
public class ModuleLandingServiceTest : IDisposable
{
    private readonly string baseDirectory =
        Path.Combine(Path.GetTempPath(), "mw-landing-" + Guid.NewGuid().ToString("N"));

    /// <summary>The RUNNING framework identity — MeshWeaver.Graph's MVID, exactly what
    /// <c>NodeTypeCompilationHelpers.FrameworkVersion</c> computes.</summary>
    private static string LiveFrameworkMvid =>
        typeof(PrebuiltAssemblySeeder).Assembly.ManifestModule.ModuleVersionId.ToString("N");

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
                packagePath: "Plugins/acme-widgets")
            .FirstAsync().ToTask();

        // The files, in the exact layout ResolveModulePath probes.
        File.ReadAllBytes(Path.Combine(baseDirectory, "modules", "Acme.Widgets", "Acme.Widgets.dll"))
            .Should().Equal([1, 2, 3]);
        File.Exists(Path.Combine(baseDirectory, "modules", "Acme.Widgets", "Acme.Widgets.Support.dll"))
            .Should().BeTrue();
        // No staging leftovers.
        Directory.GetDirectories(Path.Combine(baseDirectory, "modules"))
            .Select(Path.GetFileName)
            .Should().NotContain(n => n!.StartsWith(".staging-"));

        // The activation entry + the minimal step-10 restart-required signal.
        var list = ModuleActivationSidecar.Read(baseDirectory);
        var entry = list.Entries.Should().ContainSingle().Subject;
        entry.Name.Should().Be("Acme.Widgets");
        entry.Source.Should().Be(ModuleActivationSources.Store);
        entry.PackagePath.Should().Be("Plugins/acme-widgets");
        entry.FrameworkMvid.Should().Be(LiveFrameworkMvid);
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

        File.ReadAllBytes(Path.Combine(baseDirectory, "modules", "Acme.Widgets", "Acme.Widgets.dll"))
            .Should().Equal([9, 9], "a re-landing replaces the folder atomically");
        ModuleActivationSidecar.Read(baseDirectory).Entries
            .Should().ContainSingle("re-landing upserts the entry, never appends a duplicate");
    }

    [Fact]
    public async Task LandModule_FrameworkMvidMismatch_Refuses_NamingBothMvids()
    {
        using var service = Service;
        var foreign = Guid.NewGuid().ToString("N");

        var act = () => service
            .LandModule("Acme.Widgets", [("Acme.Widgets.dll", [1])], foreign)
            .FirstAsync().ToTask();

        (await act.Should().ThrowAsync<InvalidOperationException>(
                "landing ABI-stale bytes would surface only as a TypeLoadException at the next boot"))
            .Which.Message.Should().Contain(foreign).And.Contain(LiveFrameworkMvid,
                "the refusal must name BOTH identities so the operator can see which side is stale");

        Directory.Exists(Path.Combine(baseDirectory, "modules", "Acme.Widgets"))
            .Should().BeFalse("declined bytes never reach disk");
        ModuleActivationSidecar.Read(baseDirectory).Entries.Should().BeEmpty();
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

        Directory.Exists(Path.Combine(baseDirectory, "modules", "Acme.Widgets"))
            .Should().BeFalse("uninstall deletes the landed folder");
        var list = ModuleActivationSidecar.Read(baseDirectory);
        var entry = list.Entries.Should().ContainSingle().Subject;
        entry.Enabled.Should().BeFalse("the entry is kept, disabled — history + idempotence");
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
