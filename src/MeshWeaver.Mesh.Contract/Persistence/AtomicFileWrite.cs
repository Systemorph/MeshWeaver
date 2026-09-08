namespace MeshWeaver.Mesh.Persistence;

/// <summary>
/// The one way this framework publishes a file: write a uniquely-named temp file, then rename it
/// into place. The rename is what makes the target's APPEARANCE and its CONTENT a single event.
///
/// <para>🚨 Why it must not be <see cref="File.WriteAllTextAsync(string,string?,CancellationToken)"/>
/// or <see cref="File.WriteAllBytes(string,byte[])"/>. Both open with <c>FileMode.Create</c> — they
/// create (or truncate) the target FIRST and stream the bytes afterwards. So between the two, the
/// target exists at zero (or partial) length under its final, publicly discoverable name. Anything
/// that finds files by name is then looking at a file that is not there yet:</para>
/// <list type="bullet">
///   <item><description><c>FileSystemVersionStore.GetVersions</c> globs <c>{id}_*.json</c>, so the
///   file name IS the publication of a version. A reader that listed and then read inside that
///   window got <c>""</c> and <c>JsonSerializer.Deserialize</c> threw <i>"The input does not
///   contain any JSON tokens"</i> — how
///   <c>VersionHistoryTest.VersionQuery_GetVersionBeforeAsync_FindsPreChangeState</c> failed on CI
///   with a version list that was otherwise exactly right.</description></item>
///   <item><description><c>FileSystemAssemblyStore</c> globs <c>v{version}-{tag}-*.dll</c>, so the
///   DLL name IS the publication of a compiled NodeType assembly — and the reader hands that path
///   straight to <c>AssemblyLoadContext.LoadFromAssemblyPath</c>. A partial read there is not an
///   empty string, it is a truncated PE image: the header loads, and the first
///   <c>Assembly.GetTypes()</c> throws <c>ReflectionTypeLoadException</c> — <i>"Could not load type
///   'X' from assembly 'DynamicNode_…' because the format is invalid"</i> — which the compile
///   pipeline records as a compile failure and PARKS the NodeType (MeshWeaver#1387). On AKS the
///   cache is a ReadWriteMany Azure Files share, so the racing reader is usually a DIFFERENT
///   POD.</description></item>
///   <item><description>A cancellation landing in that same window leaves the truncated target on
///   disk permanently, with the previous content already gone — the 0-byte SamplesGraph corruption
///   pattern.</description></item>
/// </list>
///
/// <para>All three failures are the same defect: writing to the name readers watch. The cure is to
/// write somewhere readers do not look and rename.</para>
///
/// <para><b>The temp name.</b> It is a sibling of the target (rename is only atomic within one
/// filesystem) named <c>.tmp-{guid}-{targetName}.tmp</c>. The LEADING DOT and the trailing
/// <c>.tmp</c> are both load-bearing: a caller's discovery glob is anchored on the target's own
/// prefix (<c>v{version}-…</c>, <c>{id}_…</c>) and on its real extension, and a name that starts
/// with neither cannot match one — including under Windows' DOS 8.3 quirk, where a three-character
/// pattern extension such as <c>*.dll</c> also matches longer extensions that begin with it. The
/// guid makes two concurrent writers of the same target unable to collide on the temp.</para>
///
/// <para><b>Replace semantics.</b> <see cref="PublishBytes"/> deliberately does NOT replace an
/// existing target, and that is what keeps it atomic on the AKS assembly-cache share. Azure Files
/// SMB does not offer the POSIX guarantee that <c>rename()</c> over an EXISTING name is a single
/// operation, so a publisher that relied on replace would be re-introducing the very window it is
/// closing. Creating a name that does not yet exist is a single directory-entry insert on every
/// filesystem this runs on. Callers whose target names embed a content hash (the assembly store)
/// lose nothing: an existing target of that name already holds exactly these bytes.</para>
/// </summary>
public static class AtomicFileWrite
{
    /// <summary>
    /// Writes <paramref name="content"/> so that <paramref name="filePath"/> only ever exists
    /// complete. On any failure the target is left exactly as it was and no temp file survives.
    /// </summary>
    /// <param name="filePath">Final path the content is published under.</param>
    /// <param name="content">Text to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks><paramref name="content"/> is nullable to match
    /// <see cref="File.WriteAllTextAsync(string,string?,CancellationToken)"/>, which writes an
    /// empty file for <c>null</c>; callers that must never publish empty content check that
    /// themselves.</remarks>
    public static async Task WriteAllTextAsync(string filePath, string? content, CancellationToken ct)
    {
        var tempPath = TempPathFor(filePath);
        try
        {
            await File.WriteAllTextAsync(tempPath, content, ct).ConfigureAwait(false);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Publishes <paramref name="bytes"/> at <paramref name="filePath"/> so the target only ever
    /// exists complete, WITHOUT replacing an existing target.
    /// </summary>
    /// <param name="filePath">Final path the bytes are published under.</param>
    /// <param name="bytes">Bytes to write.</param>
    /// <returns><c>true</c> when this call published the file; <c>false</c> when a target of that
    /// name already existed (another writer, or another replica through a shared volume, won the
    /// race) — in which case nothing was overwritten and no temp file survives.</returns>
    /// <remarks>
    /// First-publish-wins is the contract, not an optimisation: see the type remarks on why
    /// replace-on-rename is not something the AKS assembly-cache share guarantees.
    /// </remarks>
    public static bool PublishBytes(string filePath, byte[] bytes)
        => PublishBytesWith(filePath, bytes, OpenTempForWrite);

    /// <summary>
    /// <see cref="PublishBytes(string, byte[])"/> with the staging file's writer injected — the
    /// seam a test uses to stand in for a volume that accepts a write and then does not keep it.
    /// A distinct NAME rather than an overload: an added overload turns every dependent's
    /// <c>&lt;see cref="PublishBytes"/&gt;</c> into CS0419 under <c>-warnaserror</c>, in this repo
    /// and in every satellite the next pin move reaches.
    /// </summary>
    /// <param name="filePath">Final path the bytes are published under.</param>
    /// <param name="bytes">Bytes to write.</param>
    /// <param name="openTemp">Opens the staging path for writing. Production passes a plain
    /// <see cref="FileStream"/> (see <see cref="OpenTempForWrite"/>); a test passes a stream that
    /// drops bytes past a cap, or whose flush faults, to reproduce a full share.</param>
    /// <returns>See <see cref="PublishBytes(string, byte[])"/>.</returns>
    /// <exception cref="ShortWriteException">The volume kept fewer bytes than were written — the
    /// publication is REFUSED, the staging file removed, and the target never appears.</exception>
    public static bool PublishBytesWith(string filePath, byte[] bytes, Func<string, Stream> openTemp)
    {
        ArgumentNullException.ThrowIfNull(openTemp);

        // Checked BEFORE any IO, which is what makes the catch filter below precise rather than a
        // blanket "an IOException with the target present must have been the race". Having
        // established here that the target did NOT exist, the only way it can exist by the time
        // the move fails is that another writer created it in between — a genuine fault (denied,
        // full disk, bad path) leaves the target absent, so it falls through and rethrows.
        if (File.Exists(filePath))
            return false;

        var tempPath = TempPathFor(filePath);
        try
        {
            WriteDurably(tempPath, bytes, openTemp);
            // overwrite:false → File.Move throws IOException when the target already exists.
            // That is the race being handled, not an error: the winner's bytes stay published.
            File.Move(tempPath, filePath, overwrite: false);
            return true;
        }
        // 🚨 A short write is never "the other writer won": on a full volume the other writer's
        // file is short too, and returning false would hand the caller a name whose bytes are
        // not there. The filter keeps the race branch for the RENAME alone.
        catch (IOException ex) when (ex is not ShortWriteException && File.Exists(filePath))
        {
            TryDelete(tempPath);
            return false;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// The production writer for the staging file: a plain <see cref="FileStream"/> that creates
    /// the (unique, never pre-existing) temp name. Public so a caller that injects its own writer
    /// in one arm of an experiment can name the real one in the other.
    /// </summary>
    /// <param name="tempPath">The staging path <see cref="TempPathFor"/> minted.</param>
    /// <returns>A writable stream over the staging file.</returns>
    public static Stream OpenTempForWrite(string tempPath)
        => new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024);

    /// <summary>
    /// 🚨 <b>Write, flush TO DISK, close, then prove the volume kept every byte — a write that
    /// "succeeds" is not a publication until the bytes are known to be there.</b>
    ///
    /// <para>Measured on <c>memex.systemorph.com</c>, 2026-09-08, with <c>/data</c> — the
    /// ReadWriteMany Azure Files share holding the assembly cache — at <b>3 MiB free of 16 GiB</b>:
    /// every recompile of <c>Hosting/InstanceAction</c> wrote its DLL "successfully", renamed it
    /// into the discovery namespace, minted a Release node naming it, and then the SAME pod
    /// failed to load it with <c>BadImageFormatException: Bad IL format</c>. Nothing in the
    /// pipeline had lied: the previous implementation was <c>File.WriteAllBytes</c> + rename, and
    /// on a Linux CIFS mount <c>write(2)</c> lands in the page cache and returns success — the
    /// <c>ENOSPC</c> from the server only surfaces on writeback, i.e. at <c>fsync</c> or
    /// <c>close</c>. .NET's <c>SafeFileHandle</c> discards <c>close(2)</c>'s return value, so a
    /// full share published a truncated PE image under a complete-looking name, three times in
    /// two minutes, and each load-and-delete cycle triggered the next compile.</para>
    ///
    /// <para>Two independent witnesses, because filesystems differ in which one they honour:
    /// <see cref="FileStream.Flush(bool)"/> with <c>flushToDisk</c> is the <c>fsync</c> that
    /// surfaces a writeback error as an <see cref="IOException"/> where the kernel reports one;
    /// and the length on disk after close is compared with the bytes handed in, which catches a
    /// volume that dropped bytes without reporting it at all. Either mismatch refuses the
    /// publication — the staging file is removed by the caller and the target never appears.</para>
    /// </summary>
    private static void WriteDurably(string tempPath, byte[] bytes, Func<string, Stream> openTemp)
    {
        using (var stream = openTemp(tempPath))
        {
            stream.Write(bytes, 0, bytes.Length);
            if (stream is FileStream file)
                file.Flush(flushToDisk: true);
            else
                stream.Flush();
        }

        var landed = new FileInfo(tempPath);
        var landedBytes = landed.Exists ? landed.Length : -1;
        if (landedBytes != bytes.Length)
            throw new ShortWriteException(tempPath, bytes.Length, landedBytes, VolumeCapacity.Of(tempPath));
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> so that <paramref name="filePath"/> only ever exists
    /// complete, REPLACING any existing target. Use this only where the target lives on a local
    /// filesystem — see the type remarks on why a shared SMB volume must use
    /// <see cref="PublishBytes"/> instead.
    /// </summary>
    /// <param name="filePath">Final path the bytes are published under.</param>
    /// <param name="bytes">Bytes to write.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ReplaceAllBytesAsync(string filePath, byte[] bytes, CancellationToken ct)
    {
        var tempPath = TempPathFor(filePath);
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, ct).ConfigureAwait(false);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Publishes the file produced by <paramref name="writeTemp"/> at <paramref name="filePath"/>,
    /// so the target only ever exists complete. The callback is handed the temp path and must fill
    /// it; the rename happens only after it returns. Use this when the bytes are produced by a
    /// stream-to-path API (an SDK download) rather than held in memory.
    /// </summary>
    /// <param name="filePath">Final path the file is published under.</param>
    /// <param name="writeTemp">Fills the supplied temp path.</param>
    /// <returns><c>true</c> when this call published the file; <c>false</c> when a target of that
    /// name already existed.</returns>
    public static async Task<bool> PublishAsync(string filePath, Func<string, Task> writeTemp)
    {
        // Same pre-check as PublishBytes, and for the same reason: it is what narrows the catch
        // filter below to the concurrent-writer race. See there.
        if (File.Exists(filePath))
            return false;

        var tempPath = TempPathFor(filePath);
        try
        {
            await writeTemp(tempPath).ConfigureAwait(false);
            File.Move(tempPath, filePath, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(filePath))
        {
            TryDelete(tempPath);
            return false;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// The staging name a publication of <paramref name="filePath"/> uses — a sibling that no
    /// caller's discovery glob can match. Exposed so tests can assert the invariant rather than
    /// re-derive the convention.
    /// </summary>
    /// <param name="filePath">Final path being published.</param>
    /// <returns>A unique sibling path to stage the bytes in.</returns>
    public static string TempPathFor(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        var name = $".tmp-{Guid.NewGuid():N}-{Path.GetFileName(filePath)}.tmp";
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
