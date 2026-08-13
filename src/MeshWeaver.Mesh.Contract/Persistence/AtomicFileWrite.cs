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
    {
        var tempPath = TempPathFor(filePath);
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            // overwrite:false → File.Move throws IOException when the target already exists.
            // That is the race being handled, not an error: the winner's bytes stay published.
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
