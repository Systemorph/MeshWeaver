using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A drain inside ONE module's record log stops before the next record, and is never a
/// corrupt record.</b>
///
/// <para><b>Why this test lives here.</b> The activation read checks the pool's token before each
/// record of a module's log, not only between modules (review on #7121). MeshWeaver.Plugins pinned
/// that by cancelling from the read's corrupt-record callback while the first record was read.
/// The one-line fold of record faults (MeshWeaver#7690) moved those faults into the module's drop
/// line, reported after the walk, so the cancel arrived after the last record and that test failed
/// with no exception. The product did not change: the per-record check is still there. The test
/// lost its seam. <see cref="ModuleActivationSidecar.ReadModuleLog"/> is the method that holds the
/// check, and it still reports each fault as it is met, so the cancel is injected there.</para>
///
/// <para><b>What makes this able to fail.</b> Two landing records are unreadable, and the first
/// sorts before every other record. The callback cancels on the first fault. If the per-record
/// check were removed, the walk would read the second unreadable record and report it, and only
/// the check before the NEXT record kind would throw: two reports, not one. (Measured: with the
/// check removed and one unreadable record, a test asserting only the exception still passed.)
/// If the check were moved inside the per-record catch-all, the cancel itself would be reported
/// as a corrupt record. The uncancelled control proves the walk WOULD have gone on to the second
/// unreadable record and to the real one.</para>
/// </summary>
public class DrainInsideARecordLogTest : IDisposable
{
    private const string Module = "Acme.Widgets";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-record-log-drain-" + Guid.NewGuid().ToString("N"));

    public DrainInsideARecordLogTest()
    {
        var record = new ModuleLandingRecord { Name = Module, Directory = Module + "@0123456789abcdef", Version = "1.0.0" };
        Assert.True(ModuleActivationSidecar.WriteLanding(root, record), "the landing record must be written");
        var records = ModuleActivationSidecar.LandingRecordsDirectory(root, Module);
        // Sorts before every other record, so it is read first; its fault is where the drain lands.
        File.WriteAllText(Path.Combine(records, "0-unreadable.json"), "{ not json");
        // A second unreadable record of the SAME kind, read after the first: reading it would be a
        // second report, which is how a walk that went on past the drain becomes visible.
        File.WriteAllText(Path.Combine(records, "1-unreadable.json"), "{ not json either");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* temp cleanup is the OS's problem, never a test failure */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ADrainInsideOneModulesRecordLog_StopsBeforeTheNextRecord()
    {
        using var cts = new CancellationTokenSource();
        var reports = ImmutableList.CreateBuilder<string>();

        Assert.ThrowsAny<OperationCanceledException>(() => ModuleActivationSidecar.ReadModuleLog(
            root, Module, message => { reports.Add(message); cts.Cancel(); }, cts.Token));

        // Only the first record is reported: the walk stopped before the second one, and the
        // cancel was not reported as a corrupt record.
        Assert.Single(reports);
    }

    [Fact]
    public void TheSameLog_Uncancelled_ReadsOnToTheNextRecord()
    {
        var reports = ImmutableList.CreateBuilder<string>();

        var log = ModuleActivationSidecar.ReadModuleLog(root, Module, reports.Add, CancellationToken.None);

        Assert.Equal(2, reports.Count);
        Assert.Equal(2, log.Faults);
        Assert.Single(log.Records);
    }
}
