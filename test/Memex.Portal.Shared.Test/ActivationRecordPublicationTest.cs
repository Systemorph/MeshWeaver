using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The WRITER/READER protocol for a module's activation records (MeshWeaver#2190): a record's name
/// appears holding the whole record, or it does not appear — and a publication that cannot be made
/// that way publishes NOTHING rather than something a reader can trip over.
///
/// <para><b>Why the reader has no second option.</b> A record it cannot read costs the module WHOLE
/// (#4026's fail-closed rule: an unreadable tombstone skipped would load an uninstalled module, an
/// unreadable head record would promote an older generation). At BOOT that is the process's answer
/// for its lifetime — the pod runs without the module until it is restarted. So "the reader copes"
/// is not available; the name must never be observable mid-write.</para>
///
/// <para>That is exactly what memex logged on 2026-09-16 for
/// <c>MeshWeaver.AI.Anthropic@bd2a4b60e93636cb…verdict</c>: <i>"could not be read (IOException: The
/// process cannot access the file … because it is being used by another process)"</i>, followed by
/// <i>"Module 'MeshWeaver.AI.Anthropic' is NOT loaded from this read"</i>. The first test here
/// reproduces that pairing from the reader's side; the second pins the writer's half.</para>
/// </summary>
public class ActivationRecordPublicationTest : IDisposable
{
    private const string Module = "MeshWeaver.Test.RecordPublication";
    private const string Generation = Module + "@0123456789abcdef";

    private readonly string root = Path.Combine(
        Path.GetTempPath(), "mw-record-publication-" + Guid.NewGuid().ToString("N"));

    private string? elsewhere;

    /// <summary>Creates the deployment root the records live under.</summary>
    public ActivationRecordPublicationTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup is never a failure */ }
        try { if (elsewhere is not null) Directory.Delete(elsewhere, recursive: true); } catch { /* same */ }
    }

    private static ModuleLandingRecord Landing() => new()
    {
        Name = Module,
        Source = ModuleActivationSources.Store,
        Directory = Generation,
        Version = "1.0.0",
    };

    private string RecordsDirectory => ModuleActivationSidecar.LandingRecordsDirectory(root, Module);

    /// <summary>
    /// 🚨 The production failure, from the reader's side: while ONE of a module's records is held
    /// open exclusively — which is precisely what <c>File.Move(…, overwrite: false)</c>'s copy
    /// fallback does to the final name, with <c>FileShare.None</c>, for the whole copy — the module
    /// is dropped from the read. The read is fail-closed by design, so this test is the REASON the
    /// writer may never create a final name any way but by renaming a complete file.
    /// </summary>
    [Fact]
    public void AWriterHoldingARecordsFinalNameExclusively_CostsTheWholeModule()
    {
        Assert.True(ModuleActivationSidecar.WriteLanding(root, Landing()));
        Assert.True(ModuleActivationSidecar.WriteVerdict(root, Module, Generation, "test-platform", linkable: true));

        var faults = new List<string>();
        var before = ModuleActivationSidecar.Read(root, faults.Add);
        Assert.Empty(faults);
        Assert.Contains(before.Entries, e => string.Equals(e.Name, Module, StringComparison.Ordinal));

        var verdict = Directory.GetFiles(RecordsDirectory, "*" + ModuleActivationSidecar.VerdictSuffix).Single();
        using (new FileStream(verdict, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var during = ModuleActivationSidecar.Read(root, faults.Add);

            Assert.Contains(faults, f => f.Contains(Path.GetFileName(verdict), StringComparison.Ordinal));
            Assert.Contains(faults, f => f.Contains("NOT loaded from this read", StringComparison.Ordinal));
            Assert.DoesNotContain(during.Entries, e => string.Equals(e.Name, Module, StringComparison.Ordinal));
        }

        // Released: the module is back, from the same files. Nothing about the records changed — which
        // is what makes the drop above a property of the LOCK and not of anything written.
        faults.Clear();
        var after = ModuleActivationSidecar.Read(root, faults.Add);
        Assert.Empty(faults);
        Assert.Contains(after.Entries, e => string.Equals(e.Name, Module, StringComparison.Ordinal));
    }

    /// <summary>
    /// The writer's half: where the record cannot be RENAMED into place — here because its directory
    /// is on another file system, which is the deterministic stand-in for the Azure Files rename the
    /// portals' volume refuses — the write is refused and the record directory stays empty. Before
    /// #2190 this call published a record by COPYING into the final name, which is the state the test
    /// above shows costs a module.
    /// </summary>
    [Fact]
    public void ARecordThatCannotBeRenamedIntoPlace_IsNotPublishedAtAll()
    {
        elsewhere = DirectoryOnAnotherFileSystem();
        Assert.SkipWhen(elsewhere is null,
            "this host exposes no second file system, so a refused rename cannot be staged here; the "
            + "primitive's own refusal rows are pinned in MeshWeaver.Hosting.Test's NoReplaceMoveTest.");

        Directory.CreateDirectory(ModuleActivationSidecar.EntriesDirectory(root));
        Directory.CreateSymbolicLink(RecordsDirectory, elsewhere!);

        var refusal = Assert.Throws<IOException>(() =>
            ModuleActivationSidecar.WriteVerdict(root, Module, Generation, "test-platform", linkable: true));

        Assert.Contains("NOT published", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(elsewhere!));
        var faults = new List<string>();
        Assert.Empty(ModuleActivationSidecar.Read(root, faults.Add).Entries);
        Assert.Empty(faults);
    }

    /// <summary>A writable directory on another file system, or null on a host that has none (macOS);
    /// the caller SKIPS rather than passing when this answers null.</summary>
    private static string? DirectoryOnAnotherFileSystem()
    {
        foreach (var candidate in new[] { "/dev/shm", "/run/shm" })
        {
            if (!Directory.Exists(candidate))
                continue;
            var directory = Path.Combine(candidate, "mw-record-publication-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not writable here; try the next candidate.
            }
        }
        return null;
    }
}
