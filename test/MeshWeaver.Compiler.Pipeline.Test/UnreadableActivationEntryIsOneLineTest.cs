#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>One unreadable activation entry is ONE line, and its remedy follows the fault
/// (MeshWeaver#8049).</b>
///
/// <para><b>The incident.</b> On one memex pod the open of one module's entry file,
/// <c>activation.d/&lt;Name&gt;.json</c>, was refused once by the volume (<c>EAGAIN</c> at
/// <c>open(2)</c>, no advisory lock taken). The fail-closed read then left that module out, which
/// is the designed answer. But the one fault was reported twice at Error: once as the entry fault,
/// and again as "Module … is NOT loaded from this read", because the module also has landing
/// records. The log watcher filed the two lines as two incidents about one event. The first line
/// also told the operator to "re-install it to repair the entry", although a refused open says
/// nothing about the file's content, and the second line said the next whole read restores the
/// module.</para>
///
/// <para><b>What makes this able to fail.</b> The fault is real: the entry file is made unreadable
/// (<see cref="UnreadableRecord"/>: permissions removed on Unix, held with
/// <see cref="FileShare.None"/> on Windows), so the reader's own open is refused. The test first
/// asserts that the module is genuinely dropped. It then asserts exactly one line naming the file,
/// and no re-install advice. The negative control below shows that the same fixture, readable,
/// loads the module and reports nothing.</para>
/// </summary>
public class UnreadableActivationEntryIsOneLineTest : IDisposable
{
    private const string Module = "Acme.Widgets";
    private const string Generation = Module + "@0123456789abcdef";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-unreadable-entry-" + Guid.NewGuid().ToString("N"));

    private readonly string entryPath;

    public UnreadableActivationEntryIsOneLineTest()
    {
        var generation = Path.Combine(root, "modules", Generation);
        Directory.CreateDirectory(generation);
        File.WriteAllBytes(Path.Combine(generation, Module + ".dll"), [0x4d, 0x5a]);
        var record = new ModuleLandingRecord { Name = Module, Directory = Generation, Version = "1.0.0" };
        Assert.True(ModuleActivationSidecar.WriteLanding(root, record), "the landing record must be written");
        ModuleActivationSidecar.WriteEntry(root, new ModuleActivationEntry
        {
            Name = Module,
            Directory = Generation,
            Version = "1.0.0",
        });
        entryPath = ModuleActivationSidecar.EntryPath(root, Module);
        Assert.True(File.Exists(entryPath), $"the entry must exist at '{entryPath}'");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    [Fact]
    public void AnEntryTheVolumeRefuses_DropsTheModule_InOneLine_WithoutReinstallAdvice()
    {
        var faults = ImmutableList.CreateBuilder<string>();
        ModuleActivationList list;
        using (UnreadableRecord.Hold(entryPath))
            list = ModuleActivationSidecar.Read(root, faults.Add);

        // The precondition: the read genuinely lost this module to the refused open, so one line
        // cannot come from a read that never faulted.
        Assert.DoesNotContain(list.Entries, e => e.Name == Module);

        var line = Assert.Single(faults);
        Assert.StartsWith($"Module activation entry '{entryPath}' could not be read", line);
        Assert.Contains("that ONE module is NOT loaded from this read", line);
        Assert.Contains("the next read that opens the file restores the module", line);
        Assert.Contains("nothing needs re-installing", line);
        Assert.DoesNotContain("re-install it", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("re-install the module", line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Content that is not the file's own entry is ALSO one line, and there the file
    /// itself is wrong, so the line does advise re-installing.</summary>
    [Fact]
    public void AnEntryHoldingAnotherModule_DropsTheModule_InOneLine_AndAdvisesReinstall()
    {
        File.WriteAllText(entryPath, "null");
        var faults = ImmutableList.CreateBuilder<string>();

        var list = ModuleActivationSidecar.Read(root, faults.Add);

        Assert.DoesNotContain(list.Entries, e => e.Name == Module);
        var line = Assert.Single(faults);
        Assert.StartsWith($"Module activation entry '{entryPath}' does not hold the entry", line);
        Assert.Contains("re-install the module to rewrite it", line);
    }

    /// <summary>The negative control: the same fixture, readable, loads the module and reports
    /// nothing — so the drop above is the fault's, not the fixture's.</summary>
    [Fact]
    public void AReadableEntry_LoadsTheModule_AndReportsNothing()
    {
        var faults = ImmutableList.CreateBuilder<string>();

        var list = ModuleActivationSidecar.Read(root, faults.Add);

        Assert.Empty(faults);
        Assert.Contains(list.Entries, e => e.Name == Module);
    }
}
