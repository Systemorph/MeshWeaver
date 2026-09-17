using System;
using System.Collections.Generic;
using System.IO;
using MeshWeaver.Utils;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the one property every publication on a shared volume rests on: <b>a name appears holding
/// the whole file, or it does not appear at all</b> — and, when the rename that would do that is
/// refused, NOTHING is published (MeshWeaver#2190).
///
/// <para><b>What was wrong.</b> <c>File.Move(staged, target, overwrite: false)</c> reads as an atomic
/// publish and is not one on Unix: when its <c>rename(2)</c> fails it tries <c>link(2)</c>, and on a
/// volume without hard links — Azure Files over SMB, which is the portals' <c>/data</c> — it COPIES
/// the staged file into the target, opening the target with <c>FileShare.None</c> (an exclusive
/// <c>flock</c>) and filling it afterwards. Measured on the portal image's own runtime, one such
/// move of a 512 MiB file with a reader polling the target: 21,573 opens threw <i>"The process
/// cannot access the file … because it is being used by another process"</i> and none found the
/// target absent; with file locking off (which is what a reader on ANOTHER node sees — the share is
/// mounted <c>nobrl</c>, so an <c>flock</c> never leaves the node) 52,619 opens read the target with
/// incomplete bytes. A replica reading a module's activation records inside that window drops the
/// whole module and boots without it.</para>
///
/// <para>The decision table is driven through the step seam, because no file system produces every
/// one of its rows on demand; the two real-file-system facts (publish, and refuse without replacing)
/// and the cross-volume refusal are measured against the host.</para>
/// </summary>
public class NoReplaceMoveTest : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "mw-noreplace-" + Guid.NewGuid().ToString("N"));

    /// <summary>A directory this test made on another file system, removed with the rest.</summary>
    private string? elsewhere;

    /// <summary>Creates the directory the destinations live in.</summary>
    public NoReplaceMoveTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(root, recursive: true); } catch { /* temp cleanup is never a failure */ }
        try { if (elsewhere is not null) Directory.Delete(elsewhere, recursive: true); } catch { /* same */ }
    }

    private string Staged(string name, string content)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>The ordinary case on a real file system: the whole file, under its final name, in one
    /// step — and the staging name is gone.</summary>
    [Fact]
    public void AnAbsentName_IsPublishedByOneRename()
    {
        var staged = Staged(".staged.tmp", "the record");
        var destination = Path.Combine(root, "record.verdict");

        Assert.True(NoReplaceMove.TryMove(staged, destination));

        Assert.Equal("the record", File.ReadAllText(destination));
        Assert.False(File.Exists(staged));
    }

    /// <summary>
    /// The race between two replicas writing one content-addressed name: the loser publishes nothing,
    /// REPLACES nothing — the winner's file is untouched, so a reader holding it open never sees the
    /// name vanish or change — and its staging file is left for it to remove.
    /// </summary>
    [Fact]
    public void AnExistingName_IsNeverReplaced()
    {
        var destination = Path.Combine(root, "record.verdict");
        File.WriteAllText(destination, "the winner's bytes");
        var staged = Staged(".staged.tmp", "the loser's bytes");

        Assert.False(NoReplaceMove.TryMove(staged, destination));

        Assert.Equal("the winner's bytes", File.ReadAllText(destination));
        Assert.True(File.Exists(staged), "the staging file is the caller's to remove");
    }

    /// <summary>
    /// 🚨 The regression this exists for, measured against the host: where the rename CANNOT happen —
    /// here because the staging file is on another file system — the publication is refused and the
    /// final name never appears. The control in the same setup is <see cref="File.Move(string,
    /// string, bool)"/> with <c>overwrite: false</c>, which publishes a file: that is the copy
    /// fallback, and it is what makes "the name is absent" a real assertion rather than a setup that
    /// could not have published anything either way.
    /// </summary>
    [Fact]
    public void WhenTheRenameIsImpossible_NothingIsPublished_WhereFileMoveWouldHaveCopied()
    {
        elsewhere = DirectoryOnAnotherFileSystem();
        Assert.SkipWhen(elsewhere is null,
            "this host exposes no second file system to stage from; the decision table's refusal row "
            + "is pinned through the step seam instead (see TheRefusedRename_PublishesNothing).");

        var probe = Path.Combine(elsewhere!, "probe.tmp");
        File.WriteAllText(probe, "probe");
        var probeDestination = Path.Combine(root, "probe.published");
        var crossVolume = Record.Exception(() => NoReplaceMove.TryMove(probe, probeDestination));
        Assert.SkipWhen(crossVolume is null,
            $"'{elsewhere}' turned out to be the same file system as '{root}', so a rename is possible "
            + "and this host cannot exercise the refusal against a real volume.");
        Assert.IsAssignableFrom<IOException>(crossVolume);

        var staged = Path.Combine(elsewhere!, "staged.tmp");
        File.WriteAllText(staged, "the record");
        var destination = Path.Combine(root, "record.verdict");

        Assert.Throws<IOException>(() => NoReplaceMove.TryMove(staged, destination));
        Assert.False(File.Exists(destination), "a rename that cannot happen publishes nothing");
        Assert.True(File.Exists(staged));

        // The control: the BCL call this replaced DOES put a file there, by copying into it.
        var control = Path.Combine(root, "control.verdict");
        File.Move(staged, control, overwrite: false);
        Assert.True(File.Exists(control),
            "File.Move(overwrite: false) publishes by COPY when it cannot rename — the defect");
    }

    /// <summary>A refused rename is reported, and the fallback is never reached: a volume that cannot
    /// rename must not be asked to link either, or the failure would be reported as the wrong one.</summary>
    [Fact]
    public void TheRefusedRename_PublishesNothing()
    {
        var linked = 0;
        var ops = Ops(
            rename: (_, _) => new MoveStepResult(MoveStepOutcome.Failed, 13),
            link: (_, _) => { linked++; return MoveStepResult.Done; });

        var error = Assert.Throws<IOException>(() =>
            NoReplaceMove.TryMoveWith(Staged(".staged.tmp", "x"), Path.Combine(root, "r.verdict"), ops));

        Assert.Equal(13, error.HResult);
        Assert.Contains("NOT published", error.Message);
        Assert.Equal(0, linked);
        Assert.False(File.Exists(Path.Combine(root, "r.verdict")));
    }

    /// <summary>A volume with no no-replace rename (NFS, a C library without the call) publishes
    /// through the hard link, which is the same atomic create — and then drops the staging name.</summary>
    [Fact]
    public void WhereTheVolumeCannotRenameWithoutReplacing_TheHardLinkPublishes()
    {
        var removed = new List<string>();
        var staged = Staged(".staged.tmp", "x");
        var ops = Ops(
            rename: (_, _) => new MoveStepResult(MoveStepOutcome.Unsupported, 0),
            link: (_, _) => MoveStepResult.Done,
            removeSource: removed.Add);

        Assert.True(NoReplaceMove.TryMoveWith(staged, Path.Combine(root, "r.verdict"), ops));

        Assert.Equal([Path.GetFullPath(staged)], removed);
    }

    /// <summary>The same volume, losing the race: the link reports the name is taken, and that is a
    /// refusal to publish rather than a fault.</summary>
    [Fact]
    public void WhereTheHardLinkFindsTheNameTaken_NothingIsPublished()
    {
        var ops = Ops(
            rename: (_, _) => new MoveStepResult(MoveStepOutcome.Unsupported, 0),
            link: (_, _) => new MoveStepResult(MoveStepOutcome.Exists, 17));

        Assert.False(NoReplaceMove.TryMoveWith(
            Staged(".staged.tmp", "x"), Path.Combine(root, "r.verdict"), ops));
    }

    /// <summary>
    /// 🚨 A volume that offers NEITHER atomic create is refused, loudly. That is the whole point: the
    /// alternative — copying into the final name — is the defect, so there is no code path here that
    /// can reach it.
    /// </summary>
    [Fact]
    public void AVolumeWithNeitherAtomicCreate_IsRefusedRatherThanCopiedInto()
    {
        var ops = Ops(
            rename: (_, _) => new MoveStepResult(MoveStepOutcome.Unsupported, 0),
            link: (_, _) => new MoveStepResult(MoveStepOutcome.Unsupported, 95));

        var error = Assert.Throws<IOException>(() =>
            NoReplaceMove.TryMoveWith(Staged(".staged.tmp", "x"), Path.Combine(root, "r.verdict"), ops));

        Assert.Contains("neither a no-replace rename nor hard links", error.Message);
        Assert.False(File.Exists(Path.Combine(root, "r.verdict")));
    }

    private static NoReplaceMoveOps Ops(
        Func<string, string, MoveStepResult> rename,
        Func<string, string, MoveStepResult> link,
        Action<string>? removeSource = null) =>
        new(rename, link, removeSource ?? (_ => { }), error => "error " + error);

    /// <summary>
    /// A writable directory on a file system other than the one holding <see cref="root"/>, or null
    /// when this host offers none. <c>/dev/shm</c> is a tmpfs on every Linux the mesh builds and runs
    /// on; macOS has no equivalent a test may create, which is why the caller SKIPS rather than
    /// passes when this answers null.
    /// </summary>
    private string? DirectoryOnAnotherFileSystem()
    {
        foreach (var candidate in new[] { "/dev/shm", "/run/shm" })
        {
            if (!Directory.Exists(candidate))
                continue;
            var directory = Path.Combine(candidate, "mw-noreplace-" + Guid.NewGuid().ToString("N"));
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
