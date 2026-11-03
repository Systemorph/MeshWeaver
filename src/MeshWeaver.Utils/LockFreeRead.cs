#nullable enable
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MeshWeaver.Utils;

/// <summary>
/// Opens a file that is PUBLISHED IMMUTABLE — written whole under a staging name and made visible by
/// one no-replace rename (<see cref="NoReplaceMove"/>) — for reading, WITHOUT the advisory lock .NET
/// takes on every open on Unix.
///
/// <para>🚨 <b>Why the BCL open is wrong for such a file (MeshWeaver#7690, #7797).</b> On Unix every
/// <see cref="FileStream"/>, <see cref="File.ReadAllText(string)"/> and
/// <see cref="File.OpenHandle"/> opened for reading also calls <c>flock(LOCK_SH | LOCK_NB)</c>, and
/// when that NON-BLOCKING lock answers <c>EWOULDBLOCK</c> (errno 11 on Linux) the open THROWS
/// <i>"The process cannot access the file … because it is being used by another process"</i>. On
/// the portals' Azure Files share (mounted <c>nobrl</c>, so a lock never leaves the node) that
/// answer comes from whatever else on the node holds the inode — or from the SMB client's mapping
/// of a throttled request — and the reader cannot tell which. The lock protects nothing here: a
/// file published by a no-replace rename is never written again, so there is no writer for a shared
/// lock to wait out. Taking it only adds a way for a correct read to fail. memex-cloud measured
/// exactly that: activation records failing at <c>OPEN</c> with error 11, and the module dropped
/// from that read.</para>
///
/// <para><b>What this does instead.</b> Linux and macOS: <c>open(2)</c> with
/// <c>O_RDONLY | O_CLOEXEC</c> and nothing else — no <c>flock</c>, so no other holder of the file can
/// refuse it; <c>EINTR</c> is retried as POSIX defines it. Windows: <see cref="File.OpenHandle"/>
/// with the most permissive sharing — Windows enforces share modes in the kernel, not with an
/// advisory lock, and no publisher holds a published name without sharing it.</para>
///
/// <para>🚨 <b>Only for immutable publications.</b> A file that is rewritten in place still needs a
/// writer-side exclusion; this reader does not provide one.</para>
/// </summary>
public static class LockFreeRead
{
    /// <summary>
    /// Opens <paramref name="path"/> for reading without taking an advisory lock.
    /// </summary>
    /// <param name="path">The published file.</param>
    /// <returns>A read handle the caller owns (wrap it in a <see cref="FileStream"/>).</returns>
    /// <exception cref="FileNotFoundException">No file of that name exists.</exception>
    /// <exception cref="UnauthorizedAccessException">The open was refused for permission, or the
    /// name is a directory.</exception>
    /// <exception cref="IOException">Any other refusal; <see cref="Exception.HResult"/> is the
    /// platform error number, exactly as the BCL reports it.</exception>
    public static SafeFileHandle Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (OperatingSystem.IsWindows())
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException(
                "A lock-free read is implemented for Linux, macOS and Windows only.");

        var flags = OperatingSystem.IsLinux() ? Posix.LinuxOCloexec : Posix.MacOCloexec;
        while (true)
        {
            var fd = Posix.Native.open(path, flags);
            if (fd >= 0)
                return new SafeFileHandle(fd, ownsHandle: true);
            var error = Marshal.GetLastPInvokeError();
            if (error == Posix.EINTR)
                continue;
            throw Posix.ExceptionFor(error, path);
        }
    }

    /// <summary>
    /// Reads the whole of <paramref name="path"/> as UTF-8 text AND its last write time, both from
    /// ONE lock-free handle — so the time always belongs to the bytes read, even when another
    /// publisher replaces the name meanwhile. Answers <c>null</c> when no file of that name exists.
    /// </summary>
    /// <param name="path">The published file.</param>
    /// <returns>The text and the write time, or <c>null</c> for "not there".</returns>
    /// <exception cref="LockFreeReadException">The file exists and could not be opened or read; it
    /// names the step that failed.</exception>
    public static (string Text, DateTime WrittenAtUtc)? ReadTextWithWriteTime(string path)
    {
        SafeFileHandle handle;
        try
        {
            handle = Open(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LockFreeReadException(LockFreeReadStep.Open, path, e);
        }

        using (handle)
        {
            try
            {
                var writtenAt = File.GetLastWriteTimeUtc(handle);
                using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                return (reader.ReadToEnd(), writtenAt);
            }
            // Absence after the open (the file was retired between the open and the stat on a share
            // that reports it so) is still absence, never corruption.
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new LockFreeReadException(LockFreeReadStep.Read, path, e);
            }
        }
    }

    /// <summary>
    /// Reads the whole of <paramref name="path"/> as UTF-8 text from one lock-free handle, answering
    /// <c>null</c> when no file of that name exists.
    /// </summary>
    /// <param name="path">The published file.</param>
    /// <returns>The text, or <c>null</c> for "not there".</returns>
    /// <exception cref="LockFreeReadException">The file exists and could not be opened or read.</exception>
    public static string? ReadText(string path) => ReadTextWithWriteTime(path)?.Text;

    private static class Posix
    {
        public const int EINTR = 4;
        private const int EPERM = 1;
        private const int ENOENT = 2;
        private const int EACCES = 13;
        private const int ENOTDIR = 20;
        private const int EISDIR = 21;

        // O_RDONLY is 0 on both; O_CLOEXEC is not the same number.
        public const int LinuxOCloexec = 0x80000;
        public const int MacOCloexec = 0x1000000;

        public static Exception ExceptionFor(int error, string path)
        {
            var message = $"{Marshal.GetPInvokeErrorMessage(error)} : '{path}'";
            return error switch
            {
                ENOENT => new FileNotFoundException(message, path),
                ENOTDIR => new DirectoryNotFoundException(message),
                EACCES or EPERM or EISDIR => new UnauthorizedAccessException(message),
                _ => new IOException(message, error),
            };
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles",
            Justification = "The C library's own names.")]
        public static class Native
        {
            // open(2) is variadic; only the fixed arguments are passed (no mode — this never creates),
            // which every ABI passes the same way as a non-variadic call.
            [DllImport("libc", SetLastError = true)]
            public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        }
    }
}

/// <summary>The step of a <see cref="LockFreeRead"/> that failed.</summary>
public enum LockFreeReadStep
{
    /// <summary>The <c>open(2)</c> itself — with no advisory lock, a refusal here is the file
    /// system or its transport, never another holder's lock.</summary>
    Open,

    /// <summary>The stat or read on the already-open handle.</summary>
    Read,
}

/// <summary>
/// A <see cref="LockFreeRead"/> that failed on a file that exists: the original exception rides as
/// the inner one, its message comes first (so every existing search for it still matches), and the
/// step and platform error number follow.
/// </summary>
public sealed class LockFreeReadException(LockFreeReadStep step, string path, Exception inner)
    : IOException(
        $"{inner.Message} [step: {step.ToString().ToUpperInvariant()} (lock-free: no advisory lock is "
        + $"taken); path: '{path}'; error number (HResult): {inner.HResult} (0x{inner.HResult:X8})]",
        inner)
{
    /// <summary>The step that failed.</summary>
    public LockFreeReadStep Step { get; } = step;

    /// <summary>The file that could not be read.</summary>
    public string Path { get; } = path;

    /// <summary>The platform error number of the failure.</summary>
    public int ErrorNumber { get; } = inner.HResult;
}
