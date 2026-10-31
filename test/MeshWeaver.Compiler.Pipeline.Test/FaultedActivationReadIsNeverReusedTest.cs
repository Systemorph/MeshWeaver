#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A reading of the module volume that FAULTED is never reused (MeshWeaver#7690).</b>
///
/// <para><b>The incident.</b> On memex-cloud, two activation records could not be read at the same
/// instant (<i>"The process cannot access the file … because it is being used by another
/// process"</i>), and each module was dropped from that read. The drop says "the next read that sees
/// its records whole restores it". For <see cref="PendingModuleActivations"/> that was false: it
/// keeps its reading behind a fingerprint of what the module directories LIST, and a record that
/// could not be read changes nothing that is listed. The faulted reading went on answering every
/// later probe until something unrelated landed.</para>
///
/// <para><b>What makes this able to fail.</b> The fault is real, not mocked: the test holds the
/// record open with <see cref="FileShare.None"/>, which on Unix is an exclusive advisory lock and on
/// Windows a sharing violation, so the reader's own open is refused. The test first asserts that
/// the reading DID fault, then releases the record without touching its name, length or write time,
/// so the fingerprint is byte-identical, and asserts that the next reading is whole.</para>
/// </summary>
public class FaultedActivationReadIsNeverReusedTest : IDisposable
{
    private const string Module = "Acme.Widgets";
    private const string Generation = Module + "@0123456789abcdef";

    private static readonly IReadOnlySet<string> NothingLoaded =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-faulted-read-" + Guid.NewGuid().ToString("N"));

    private readonly string recordPath;

    public FaultedActivationReadIsNeverReusedTest()
    {
        var generation = Path.Combine(root, "modules", Generation);
        Directory.CreateDirectory(generation);
        File.WriteAllBytes(Path.Combine(generation, Module + ".dll"), [0x4d, 0x5a]);
        var record = new ModuleLandingRecord { Name = Module, Directory = Generation, Version = "1.0.0" };
        Assert.True(ModuleActivationSidecar.WriteLanding(root, record), "the landing record must be written");
        recordPath = ModuleActivationSidecar.LandingRecordPath(root, record);
        Assert.True(File.Exists(recordPath), $"the record must exist at '{recordPath}'");
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    [Fact]
    public void AReadingThatFaulted_IsTakenAgain_AndTheModuleReadsWholeOnceTheRecordCanBeRead()
    {
        var reader = new PendingModuleActivations(root);

        ModuleActivationReport faulted;
        using (new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.None))
            faulted = reader.Read(NothingLoaded);

        // The precondition: the reading genuinely faulted on THIS record, so what follows cannot
        // pass because the record was never unreadable.
        Assert.True(faulted.IsUndetermined, "a record held exclusively must fault the reading");
        Assert.Contains(Module, faulted.UndeterminedReason);
        Assert.Equal(1, reader.DiskReads);

        // The record is released untouched: same name, same length, same write time, so the
        // listing fingerprint is identical to the one the faulted reading was taken under.
        var healed = reader.Read(NothingLoaded);

        Assert.Equal(2, reader.DiskReads);
        Assert.False(healed.IsUndetermined,
            $"the record is readable again, so the module must read whole: {healed.UndeterminedReason}");
    }

    /// <summary>The negative control: a WHOLE reading is still kept behind the fingerprint, so the
    /// fix did not buy the healing by giving up the memo that keeps probes off the volume.</summary>
    [Fact]
    public void AWholeReading_IsStillReused_WhileNothingOnTheVolumeChanges()
    {
        var reader = new PendingModuleActivations(root);

        var first = reader.Read(NothingLoaded);
        var second = reader.Read(NothingLoaded);

        Assert.False(first.IsUndetermined, first.UndeterminedReason);
        Assert.False(second.IsUndetermined, second.UndeterminedReason);
        Assert.Equal(1, reader.DiskReads);
    }

    /// <summary>The diagnostic (MeshWeaver#7689): a refused open names its step, the path and the
    /// error number, and keeps .NET's original message first so existing searches still match.</summary>
    [Fact]
    public void ARefusedRecordRead_NamesTheStepThePathAndTheErrorNumber()
    {
        var reasons = new List<string>();
        using (new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.None))
            ModuleActivationSidecar.Read(root, reasons.Add);

        var reason = Assert.Single(reasons, r => r.StartsWith("Module record '", StringComparison.Ordinal));
        Assert.Contains("step: OPEN", reason);
        Assert.Contains(recordPath, reason);
        Assert.Contains("error number (HResult):", reason);
    }
}
