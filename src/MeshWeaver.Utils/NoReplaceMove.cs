#nullable enable
using System.Runtime.InteropServices;

namespace MeshWeaver.Utils;

/// <summary>
/// Publishes a completely written staging file under a name that does not exist yet as ONE rename —
/// or not at all. Never a replace, and never a copy.
///
/// <para>🚨 <b>Why <see cref="File.Move(string, string, bool)"/> with <c>overwrite: false</c> is not
/// this (MeshWeaver#2190).</b> On Unix, .NET checks that the destination is absent and calls
/// <c>rename(2)</c>. When that rename FAILS for any reason, it does not report the failure: it tries
/// <c>link(2)</c>, and when the volume has no hard links it COPIES the staging file into the final
/// name — opening that name with <c>FileShare.None</c>, which on Unix is an exclusive
/// <c>flock</c>, and filling it afterwards (<c>FileSystem.Unix.cs</c>, <c>LinkOrCopyFile</c>). For
/// the whole copy the final name exists, holds incomplete bytes, and is exclusively locked. Azure
/// Files over SMB has no hard links, so on the portals' shared <c>/data</c> volume every failed
/// rename published that way. Measured with the portal image's own .NET runtime, a reader polling a
/// 512 MiB record during one such move: 21,573 opens failed with <i>"The process cannot access the
/// file '…' because it is being used by another process"</i> — the exact line memex logged for an
/// activation verdict on 2026-09-16 — and zero found the name absent; with the lock taken out of
/// the picture (a reader on another node: the share is mounted <c>nobrl</c>, so a <c>flock</c>
/// never leaves the node) 52,619 opens read the final name with its bytes incomplete.</para>
///
/// <para><b>What this does instead.</b> Linux: <c>renameat2(RENAME_NOREPLACE)</c>, which the CIFS
/// client sends as the server's native no-replace rename, so "absent → published" is one server
/// operation and "present" is <c>EEXIST</c> with nothing touched. macOS:
/// <c>renamex_np(RENAME_EXCL)</c>. A volume that cannot do a no-replace rename (NFS, some FUSE
/// file systems, a C library without the call) falls to <c>link(2)</c>, which is the same atomic
/// no-replace create where hard links exist. A volume with neither is REFUSED with an
/// <see cref="IOException"/>: a publication that cannot be atomic is not attempted. Windows:
/// <c>MoveFileEx</c> without replace, which is a rename on one volume — and a staging file on
/// another volume is refused rather than copied.</para>
///
/// <para>Every failure other than "the name already exists" throws, carries the <c>errno</c> as
/// <see cref="Exception.HResult"/>, and leaves the destination exactly as it was. The staging file
/// is never deleted here: on <c>false</c> and on a throw it is still the caller's to remove.</para>
/// </summary>
public static class NoReplaceMove
{
    /// <summary>
    /// Moves <paramref name="sourcePath"/> to <paramref name="destinationPath"/> when no file of that
    /// name exists, as one atomic operation.
    /// </summary>
    /// <param name="sourcePath">The completely written staging file. Keep it on the same volume as the
    /// destination — a sibling directory — or the move is refused.</param>
    /// <param name="destinationPath">The final name readers discover.</param>
    /// <returns><c>true</c> when this call published the file (the staging name is gone);
    /// <c>false</c> when a file of that name already existed — nothing was replaced and the staging
    /// file is still there.</returns>
    /// <exception cref="IOException">The rename was refused for any other reason. Nothing was written
    /// under <paramref name="destinationPath"/>; <see cref="Exception.HResult"/> is the platform
    /// error number.</exception>
    public static bool TryMove(string sourcePath, string destinationPath)
        => TryMoveWith(sourcePath, destinationPath, NoReplaceMoveOps.ForThisPlatform);

    /// <summary>
    /// <see cref="TryMove"/> with the platform steps injected — the seam that pins the decision
    /// table (which step runs after which outcome) on every host, including the outcomes a test
    /// cannot make a real file system produce on demand. A distinct NAME rather than an overload,
    /// so no <c>cref</c> to <see cref="TryMove"/> becomes ambiguous.
    /// </summary>
    internal static bool TryMoveWith(string sourcePath, string destinationPath, NoReplaceMoveOps ops)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ArgumentNullException.ThrowIfNull(ops);
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);

        var renamed = ops.RenameNoReplace(source, destination);
        switch (renamed.Outcome)
        {
            case MoveStepOutcome.Done:
                return true;
            case MoveStepOutcome.Exists:
                return false;
            case MoveStepOutcome.Failed:
                throw Refused(source, destination, "the no-replace rename failed", renamed.Error, ops);
        }

        // The volume cannot rename without replacing. A hard link is the same atomic no-replace
        // create where the volume has them; the staging name is then removed.
        var linked = ops.Link(source, destination);
        switch (linked.Outcome)
        {
            case MoveStepOutcome.Done:
                // The final name is complete from here on — it IS the staging file's inode. A staging
                // name that cannot be removed is residue for the caller's own sweep, never a reason to
                // report the publication as failed.
                ops.RemoveSource(source);
                return true;
            case MoveStepOutcome.Exists:
                return false;
            case MoveStepOutcome.Unsupported:
                throw Refused(source, destination,
                    "this volume supports neither a no-replace rename nor hard links, so the file "
                    + "cannot be published atomically", linked.Error, ops);
            default:
                throw Refused(source, destination, "the hard link failed", linked.Error, ops);
        }
    }

    private static IOException Refused(
        string source, string destination, string what, int error, NoReplaceMoveOps ops) =>
        new($"'{destination}' was NOT published: {what} ({ops.Describe(error)}, error {error}) moving "
            + $"'{source}'. Nothing was written under the final name, and a copy is deliberately not "
            + "attempted: a copy makes the name visible before its bytes are complete, and on Unix "
            + "holds it under an exclusive lock, which is exactly what a concurrent reader must never "
            + "see (MeshWeaver#2190).", error);
}

/// <summary>How one platform step of <see cref="NoReplaceMove"/> ended.</summary>
internal enum MoveStepOutcome
{
    /// <summary>The destination now holds the staging file.</summary>
    Done,

    /// <summary>A file of the destination's name already existed; nothing was changed.</summary>
    Exists,

    /// <summary>The volume or the platform does not offer this step; the next one may.</summary>
    Unsupported,

    /// <summary>The step was refused for any other reason.</summary>
    Failed,
}

/// <summary>One platform step's outcome and the platform error number behind it (0 on success).</summary>
internal readonly record struct MoveStepResult(MoveStepOutcome Outcome, int Error)
{
    public static MoveStepResult Done => new(MoveStepOutcome.Done, 0);
}

/// <summary>
/// The platform steps <see cref="NoReplaceMove"/> composes. Production uses
/// <see cref="ForThisPlatform"/>; a test injects its own to drive every row of the decision table.
/// </summary>
internal sealed record NoReplaceMoveOps(
    Func<string, string, MoveStepResult> RenameNoReplace,
    Func<string, string, MoveStepResult> Link,
    Action<string> RemoveSource,
    Func<int, string> Describe)
{
    /// <summary>The steps of the platform this process runs on.</summary>
    public static NoReplaceMoveOps ForThisPlatform { get; } =
        OperatingSystem.IsLinux() ? Posix.Linux
        : OperatingSystem.IsMacOS() ? Posix.MacOs
        : OperatingSystem.IsWindows() ? WindowsOps()
        : new NoReplaceMoveOps(
            (_, _) => throw new PlatformNotSupportedException(
                "An atomic no-replace move is implemented for Linux, macOS and Windows only."),
            (_, _) => new MoveStepResult(MoveStepOutcome.Unsupported, 0),
            _ => { },
            error => $"platform error {error}");

    /// <summary>
    /// The Windows step: <c>MoveFileExW</c> with <b>no flags at all</b>.
    ///
    /// <para>🚨 Deliberately NOT <see cref="File.Move(string,string,bool)"/> (Copilot's review of
    /// #4547). The BCL passes <c>MOVEFILE_COPY_ALLOWED</c>, so across volumes it COPIES — the very
    /// thing this primitive exists to remove — and a path-root comparison does not catch the case
    /// that matters: a volume mounted INTO a folder shares its root with the volume it is mounted
    /// on. With the flag absent the call cannot copy — a cross-volume move is refused with
    /// <c>ERROR_NOT_SAME_DEVICE</c>, and an existing target answers
    /// <c>ERROR_ALREADY_EXISTS</c>/<c>ERROR_FILE_EXISTS</c>, which is the "another writer published
    /// it first" outcome.</para>
    /// </summary>
    private static NoReplaceMoveOps WindowsOps() => new(
        (source, destination) =>
        {
            const int errorFileExists = 80;
            const int errorAlreadyExists = 183;
            if (WindowsNative.MoveFileExW(source, destination, 0))
                return MoveStepResult.Done;
            var error = Marshal.GetLastPInvokeError();
            return new MoveStepResult(
                error is errorFileExists or errorAlreadyExists
                    ? MoveStepOutcome.Exists
                    : MoveStepOutcome.Failed,
                error);
        },
        // NTFS has hard links, but the step above never answers Unsupported: a rename it cannot make
        // is REFUSED, and without MOVEFILE_COPY_ALLOWED it cannot have copied anything on the way.
        (_, _) => new MoveStepResult(MoveStepOutcome.Unsupported, 0),
        _ => { },
        Marshal.GetPInvokeErrorMessage);

    private static class WindowsNative
    {
        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true,
            CharSet = CharSet.Unicode, BestFitMapping = false)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveFileExW(
            [MarshalAs(UnmanagedType.LPWStr)] string existingFileName,
            [MarshalAs(UnmanagedType.LPWStr)] string newFileName,
            uint flags);
    }

    /// <summary>The POSIX steps, parameterised by the one platform difference that matters here: the
    /// error numbers and the no-replace rename call.</summary>
    private static class Posix
    {
        // errno values. EEXIST, EPERM, EINVAL agree across Linux and macOS; the rest do not.
        private const int EPERM = 1;
        private const int EEXIST = 17;
        private const int EINVAL = 22;
        private const int EMLINK = 31;

        private const int LinuxEnosys = 38;
        private const int LinuxEopnotsupp = 95;

        private const int MacEnotsup = 45;
        private const int MacEnosys = 78;
        private const int MacEopnotsupp = 102;

        private const int AtFdCwd = -100;
        private const uint RenameNoReplace = 1;
        private const uint RenameExcl = 4;

        public static NoReplaceMoveOps Linux { get; } = new(
            (source, destination) => Rename(
                () => Native.renameat2(AtFdCwd, source, AtFdCwd, destination, RenameNoReplace),
                unsupported: [EINVAL, LinuxEnosys, LinuxEopnotsupp]),
            (source, destination) => Link(source, destination, unsupported: [EPERM, EMLINK, LinuxEnosys, LinuxEopnotsupp]),
            RemoveSource,
            Describe);

        public static NoReplaceMoveOps MacOs { get; } = new(
            (source, destination) => Rename(
                () => Native.renamex_np(source, destination, RenameExcl),
                unsupported: [EINVAL, MacEnotsup, MacEnosys, MacEopnotsupp]),
            (source, destination) => Link(source, destination, unsupported: [EPERM, EMLINK, MacEnotsup, MacEnosys, MacEopnotsupp]),
            RemoveSource,
            Describe);

        private static MoveStepResult Rename(Func<int> call, int[] unsupported)
        {
            int result;
            try
            {
                result = call();
            }
            catch (EntryPointNotFoundException)
            {
                // A C library without the call (older glibc, musl): the volume may still link.
                return new MoveStepResult(MoveStepOutcome.Unsupported, 0);
            }
            if (result == 0)
                return MoveStepResult.Done;
            var error = Marshal.GetLastPInvokeError();
            return new MoveStepResult(
                error == EEXIST ? MoveStepOutcome.Exists
                : unsupported.Contains(error) ? MoveStepOutcome.Unsupported
                : MoveStepOutcome.Failed,
                error);
        }

        private static MoveStepResult Link(string source, string destination, int[] unsupported)
        {
            if (Native.link(source, destination) == 0)
                return MoveStepResult.Done;
            var error = Marshal.GetLastPInvokeError();
            return new MoveStepResult(
                error == EEXIST ? MoveStepOutcome.Exists
                : unsupported.Contains(error) ? MoveStepOutcome.Unsupported
                : MoveStepOutcome.Failed,
                error);
        }

        // The return value is deliberately not inspected: the publication is already complete, and a
        // staging name left behind is residue the caller's sweep removes (see TryMoveWith).
        private static void RemoveSource(string source) => Native.unlink(source);

        private static string Describe(int error) => Marshal.GetPInvokeErrorMessage(error);

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles",
            Justification = "The C library's own names.")]
        private static class Native
        {
            [DllImport("libc", SetLastError = true)]
            public static extern int renameat2(
                int olddirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath,
                int newdirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath, uint flags);

            [DllImport("libc", SetLastError = true)]
            public static extern int renamex_np(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string from,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);

            [DllImport("libc", SetLastError = true)]
            public static extern int link(
                [MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath,
                [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath);

            [DllImport("libc", SetLastError = true)]
            public static extern int unlink([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        }
    }
}
