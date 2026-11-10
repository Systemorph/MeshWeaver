using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>One unreadable landing RECORD is ONE line (MeshWeaver#7690).</b>
///
/// <para><b>The incident.</b> On one memex pod the open of one record under
/// <c>activation.d/&lt;Name&gt;/</c> (a <c>.verdict</c> file) was refused once by the volume
/// (<c>EAGAIN</c> at <c>open(2)</c>, no advisory lock taken). The fail-closed read then left that
/// module out, which is the designed answer. But the one fault was reported twice at Error: once
/// as "Module record … could not be read", and two seconds later as "Module … is NOT loaded from
/// this read: 1 of its records could not be read". The log watcher filed the two lines as two
/// incidents about one event. #8067 removed the same double line for the module's ENTRY file;
/// this is the record half of that defect.</para>
///
/// <para><b>What makes this able to fail.</b> The fault is real: the record file is made
/// unreadable (<see cref="UnreadableRecord"/>), so the reader's own open is refused. The test first
/// asserts that the module is genuinely dropped, then exactly one line that names BOTH the record
/// and the consequence. The negative control shows the same fixture, readable, loads the module
/// and reports nothing.</para>
/// </summary>
public class UnreadableLandingRecordIsOneLineTest : IDisposable
{
    private const string Module = "Acme.Widgets";
    private const string Generation = Module + "@0123456789abcdef";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-unreadable-record-" + Guid.NewGuid().ToString("N"));

    private readonly string recordPath;

    /// <summary>Lands one module with one landing record and its own readable entry file.</summary>
    public UnreadableLandingRecordIsOneLineTest()
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
        var records = Directory.GetFiles(
            Path.Combine(ModuleActivationSidecar.EntriesDirectory(root), Module), "*.json");
        recordPath = Assert.Single(records);
    }

    /// <summary>Removes the temporary module root.</summary>
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    /// <summary>The incident: the volume refuses one record's open. The module is dropped, and
    /// the fault is ONE line naming the record, the refusal and the consequence.</summary>
    [Fact]
    public void ARecordTheVolumeRefuses_DropsTheModule_InOneLine()
    {
        var faults = ImmutableList.CreateBuilder<string>();
        ModuleActivationList list;
        using (UnreadableRecord.Hold(recordPath))
            list = ModuleActivationSidecar.Read(root, faults.Add);

        // The precondition: the read genuinely lost this module to the refused open.
        Assert.DoesNotContain(list.Entries, e => e.Name == Module);

        var line = Assert.Single(faults);
        Assert.StartsWith($"Module '{Module}' is NOT loaded from this read: 1 of its records could not be read: ", line);
        Assert.Contains($"Module record '{recordPath}' could not be read (", line);
        Assert.Contains("the next read that sees its records whole restores it", line);
        Assert.DoesNotContain("..", line);
    }

    /// <summary>A record whose content is not the record its name addresses is ALSO one line.</summary>
    [Fact]
    public void ARecordHoldingAnotherRecord_DropsTheModule_InOneLine()
    {
        File.WriteAllText(recordPath, "null");
        var faults = ImmutableList.CreateBuilder<string>();

        var list = ModuleActivationSidecar.Read(root, faults.Add);

        Assert.DoesNotContain(list.Entries, e => e.Name == Module);
        var line = Assert.Single(faults);
        Assert.StartsWith($"Module '{Module}' is NOT loaded from this read: 1 of its records could not be read: ", line);
        Assert.Contains($"Module record '{recordPath}' does not match its own name", line);
    }

    /// <summary>The negative control: the same fixture, readable, loads the module and reports
    /// nothing — so the drop above is the fault's, not the fixture's.</summary>
    [Fact]
    public void ReadableRecords_LoadTheModule_AndReportNothing()
    {
        var faults = ImmutableList.CreateBuilder<string>();

        var list = ModuleActivationSidecar.Read(root, faults.Add);

        Assert.Empty(faults);
        Assert.Contains(list.Entries, e => e.Name == Module);
    }
}
