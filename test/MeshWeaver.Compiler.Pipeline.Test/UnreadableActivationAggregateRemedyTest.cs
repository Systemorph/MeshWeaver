using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>An unreadable legacy activation aggregate names the remedy its fault shows
/// (MeshWeaver#8124).</b>
///
/// <para><b>The incident.</b> On one memex pod the open of the legacy aggregate
/// <c>modules/activation.json</c> was refused once by the volume (<c>EAGAIN</c> at <c>open(2)</c>,
/// no advisory lock taken). Skipping its entries for that read is the designed answer. But the
/// line told the operator that the modules recorded there "will NOT load until the file is
/// repaired or the modules are re-installed", although a refused open says nothing about the
/// file's content and the next read that opens it restores them. #8049 fixed the same wrong
/// advice for a per-module entry file; the aggregate kept it.</para>
///
/// <para><b>What makes this able to fail.</b> The fault is real: the aggregate is made unreadable
/// (<see cref="UnreadableRecord"/>), so the reader's own open is refused. The test first asserts
/// that the module recorded only there is genuinely absent from the read. The negative control
/// shows that the same fixture, readable, loads the module and reports nothing.</para>
/// </summary>
public class UnreadableActivationAggregateRemedyTest : IDisposable
{
    private const string Module = "Acme.Legacy";
    private const string Generation = Module + "@0123456789abcdef";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-unreadable-aggregate-" + Guid.NewGuid().ToString("N"));

    private readonly string aggregatePath;

    /// <summary>Lands one module recorded ONLY in the legacy aggregate (no per-module entry file,
    /// no landing records), with its generation's bytes present.</summary>
    public UnreadableActivationAggregateRemedyTest()
    {
        var generation = Path.Combine(root, "modules", Generation);
        Directory.CreateDirectory(generation);
        File.WriteAllBytes(Path.Combine(generation, Module + ".dll"), [0x4d, 0x5a]);
        aggregatePath = ModuleActivationSidecar.SidecarPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(aggregatePath)!);
        var list = new ModuleActivationList
        {
            Entries = [new ModuleActivationEntry { Name = Module, Directory = Generation, Version = "1.0.0" }],
        };
        File.WriteAllText(aggregatePath, JsonSerializer.Serialize(list, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    /// <summary>Removes the temporary module root.</summary>
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    /// <summary>The incident: the volume refuses the aggregate's open. Its entries are skipped,
    /// and the line says nothing needs repairing or re-installing.</summary>
    [Fact]
    public void AnAggregateTheVolumeRefuses_SkipsItsEntries_WithoutRepairAdvice()
    {
        var faults = ImmutableList.CreateBuilder<string>();
        ModuleActivationList list;
        using (UnreadableRecord.Hold(aggregatePath))
            list = ModuleActivationSidecar.Read(root, faults.Add);

        // The precondition: the read genuinely lost this module to the refused open.
        Assert.DoesNotContain(list.Entries, e => e.Name == Module);

        var line = Assert.Single(faults);
        Assert.StartsWith($"Module activation sidecar '{aggregatePath}' could not be read", line);
        Assert.Contains("nothing needs repairing or re-installing", line);
        Assert.Contains("the next read that opens the file restores the entries it holds", line);
        Assert.DoesNotContain("will NOT load", line, StringComparison.Ordinal);
        Assert.DoesNotContain("re-install the modules", line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Bytes that do not parse are a content fault, and there the line does advise
    /// repairing the file or re-installing.</summary>
    [Fact]
    public void AnAggregateThatDoesNotParse_SkipsItsEntries_AndAdvisesRepair()
    {
        File.WriteAllText(aggregatePath, "{ this is not an activation list");
        var faults = ImmutableList.CreateBuilder<string>();

        var list = ModuleActivationSidecar.Read(root, faults.Add);

        Assert.DoesNotContain(list.Entries, e => e.Name == Module);
        var line = Assert.Single(faults);
        Assert.StartsWith($"Module activation sidecar '{aggregatePath}' could not be read (JsonException", line);
        Assert.Contains("will NOT load until the file is repaired or the modules are re-installed", line);
    }

    /// <summary>The negative control: the same fixture, readable, loads the module and reports
    /// nothing — so the absence above is the fault's, not the fixture's.</summary>
    [Fact]
    public void AReadableAggregate_LoadsTheModule_AndReportsNothing()
    {
        var faults = ImmutableList.CreateBuilder<string>();

        var list = ModuleActivationSidecar.Read(root, faults.Add);

        Assert.Empty(faults);
        Assert.Contains(list.Entries, e => e.Name == Module);
    }
}
