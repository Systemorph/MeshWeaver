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
/// <para><b>What makes this able to fail.</b> The fault is real: the entry file's open is refused
/// (<see cref="RefusedRecord"/>: a refusal that is not a denial, the incident's class) or denied
/// (<see cref="UnreadableRecord"/>: permissions removed, Unix), so the reader's own open fails. The test first
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

    /// <summary>Lands one module with a landing record AND its own readable entry file.</summary>
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

    /// <summary>Removes the temporary module root.</summary>
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        // Both faults a delete can raise: a file in use (IOException) and one this process may not
        // remove (UnauthorizedAccessException, e.g. read-only on Windows).
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    /// <summary>The incident: the volume refuses the entry's open (<see cref="RefusedRecord"/>, a
    /// refusal that is not a denial). The module is dropped, the fault is ONE line, and the line
    /// advises no re-install.</summary>
    [Fact]
    public void AnEntryTheVolumeRefuses_DropsTheModule_InOneLine_WithoutReinstallAdvice()
    {
        var faults = ImmutableList.CreateBuilder<string>();
        ModuleActivationList list;
        using (RefusedRecord.Hold(entryPath))
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
        Assert.DoesNotContain("was denied", line, StringComparison.Ordinal);
    }

    /// <summary>A DENIED read (MeshWeaver#8124) says nothing about the content either, so no
    /// re-install; but a denied permission does not lift by itself, so the line names the
    /// permission or the mount as what to fix and promises no self-repair. Unix only: there
    /// <see cref="UnreadableRecord"/> removes every permission; on Windows it is a sharing violation.</summary>
    [Fact]
    public void AnEntryWhoseReadIsDenied_DropsTheModule_InOneLine_AndNamesThePermission()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a permission denial is provoked by chmod on Unix only");
        var faults = ImmutableList.CreateBuilder<string>();
        ModuleActivationList list;
        using (UnreadableRecord.Hold(entryPath))
            list = ModuleActivationSidecar.Read(root, faults.Add);

        Assert.DoesNotContain(list.Entries, e => e.Name == Module);
        var line = Assert.Single(faults);
        Assert.StartsWith($"Module activation entry '{entryPath}' could not be read", line);
        Assert.Contains("that ONE module is NOT loaded from this read", line);
        Assert.Contains("Access to the file was denied", line);
        Assert.Contains("nothing needs re-installing", line);
        Assert.Contains("fix the file's permissions or the volume's mount", line);
        Assert.DoesNotContain("the next read that opens the file restores", line, StringComparison.Ordinal);
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

    /// <summary>Bytes that do not parse are a content fault: one line, and it advises re-installing.</summary>
    [Fact]
    public void AnEntryThatDoesNotParse_DropsTheModule_InOneLine_AndAdvisesReinstall()
    {
        File.WriteAllText(entryPath, "{ this is not an activation entry");
        var faults = ImmutableList.CreateBuilder<string>();

        var list = ModuleActivationSidecar.Read(root, faults.Add);

        Assert.DoesNotContain(list.Entries, e => e.Name == Module);
        var line = Assert.Single(faults);
        Assert.StartsWith($"Module activation entry '{entryPath}' could not be read (JsonException", line);
        Assert.Contains("does not parse as an activation entry, so re-install the module", line);
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
