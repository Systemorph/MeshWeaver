using System;
using System.IO;
using MeshWeaver.Utils;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Pins the property every reader of an immutable publication on a shared volume rests on: <b>a
/// published file reads whole whatever else holds it</b> (MeshWeaver#7690, #7797).
///
/// <para><b>What was wrong.</b> On Unix every BCL open for reading also takes
/// <c>flock(LOCK_SH | LOCK_NB)</c>, and when that non-blocking lock answers <c>EWOULDBLOCK</c> the
/// open throws <i>"being used by another process"</i>. memex-cloud's module activation records
/// failed that way at OPEN with error 11, and each read dropped the whole module. The records are
/// published by a no-replace rename and never rewritten, so the lock protects nothing.</para>
///
/// <para><b>How the fault is made real.</b> Two handles on one file: the first holds it with
/// <see cref="FileShare.None"/> — on Unix an exclusive <c>flock</c>, which conflicts with any other
/// open file description, in this process or another — and the BCL open is measured to FAIL in the
/// same window (the negative control), so the lock-free read cannot pass on a lock that was never
/// held.</para>
/// </summary>
public class LockFreeReadTest : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-lockfree-read-" + Guid.NewGuid().ToString("N"));

    public LockFreeReadTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* temp cleanup is the OS's problem, never a test failure */ }
    }

    [Fact]
    public void AFileHeldUnderAnExclusiveLock_ReadsWhole_WhereTheBclOpenIsRefused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "advisory flock semantics are Unix-only");
        var path = Path.Combine(root, "record.json");
        File.WriteAllText(path, "{\"name\":\"Acme.Widgets\"}");
        var writtenAt = File.GetLastWriteTimeUtc(path);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // The negative control: the BCL reader IS refused by the holder, exactly as in production.
            var refused = Assert.ThrowsAny<IOException>(() => File.ReadAllText(path));
            Assert.Contains("being used by another process", refused.Message);

            var read = LockFreeRead.ReadTextWithWriteTime(path);

            Assert.NotNull(read);
            Assert.Equal("{\"name\":\"Acme.Widgets\"}", read.Value.Text);
            Assert.Equal(writtenAt, read.Value.WrittenAtUtc);
        }
    }

    [Fact]
    public void ALockFreeRead_DoesNotItselfLock_SoAnExclusiveOpenAfterItSucceeds()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "advisory flock semantics are Unix-only");
        var path = Path.Combine(root, "record.json");
        File.WriteAllText(path, "x");

        using var handle = LockFreeRead.Open(path);
        // The negative control first: a BCL read handle DOES hold a shared lock that refuses this.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.ThrowsAny<IOException>(() =>
                new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose());

        new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
    }

    [Fact]
    public void AnAbsentFile_IsAbsence_NeverAFault()
    {
        Assert.Null(LockFreeRead.ReadTextWithWriteTime(Path.Combine(root, "absent.json")));
        Assert.Null(LockFreeRead.ReadText(Path.Combine(root, "no-such-dir", "absent.json")));
        Assert.Throws<FileNotFoundException>(() => LockFreeRead.Open(Path.Combine(root, "absent.json")).Dispose());
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void AFileThatExistsButCannotBeOpened_FaultsAtOpen_NamingThePathAndTheErrorNumber()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");
        var path = Path.Combine(root, "record.json");
        File.WriteAllText(path, "x");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var fault = Assert.Throws<LockFreeReadException>(() => LockFreeRead.ReadText(path));
            Assert.Equal(LockFreeReadStep.Open, fault.Step);
            Assert.Equal(path, fault.Path);
            Assert.IsType<UnauthorizedAccessException>(fault.InnerException);
            Assert.Contains("step: OPEN", fault.Message);
            Assert.Contains(path, fault.Message);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void ADirectory_IsRefused_NotReadAsAFile()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "open(2) of a directory is a Unix shape");
        var directory = Path.Combine(root, "a-directory");
        Directory.CreateDirectory(directory);
        // open(2) of a directory with O_RDONLY succeeds; the read is what refuses (EISDIR).
        var fault = Assert.Throws<LockFreeReadException>(() => LockFreeRead.ReadText(directory));
        Assert.Equal(LockFreeReadStep.Read, fault.Step);
    }
}
