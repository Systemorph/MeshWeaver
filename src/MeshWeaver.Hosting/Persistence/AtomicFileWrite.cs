namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// The one way this assembly publishes a file: write a uniquely-named temp file, then rename it
/// over the target. The rename is what makes the target's APPEARANCE and its CONTENT a single
/// event.
///
/// <para>🚨 Why it must not be <see cref="File.WriteAllTextAsync(string,string?,CancellationToken)"/>.
/// That opens with <c>FileMode.Create</c> — it creates (or truncates) the target FIRST and streams
/// the bytes afterwards. So between the two, the target exists at zero length under its final,
/// publicly discoverable name. Anything that finds files by name is then looking at a file that is
/// not there yet:</para>
/// <list type="bullet">
///   <item><description><c>FileSystemVersionStore.GetVersions</c> globs <c>{id}_*.json</c>, so the
///   file name IS the publication of a version. A reader that listed and then read inside that
///   window got <c>""</c> and <c>JsonSerializer.Deserialize</c> threw <i>"The input does not
///   contain any JSON tokens"</i> — how
///   <c>VersionHistoryTest.VersionQuery_GetVersionBeforeAsync_FindsPreChangeState</c> failed on CI
///   with a version list that was otherwise exactly right.</description></item>
///   <item><description>A cancellation landing in that same window leaves the truncated target on
///   disk permanently, with the previous content already gone — the 0-byte SamplesGraph corruption
///   pattern.</description></item>
/// </list>
///
/// <para>Both failures are the same defect: writing to the name readers watch. The cure is to write
/// somewhere readers do not look and rename, which is atomic on every filesystem this runs on. The
/// temp name deliberately keeps the target's full name as a prefix and appends <c>.tmp.{guid}</c>,
/// so it can never match a <c>*.json</c> glob, and two concurrent writers of the same target cannot
/// collide on the temp.</para>
/// </summary>
internal static class AtomicFileWrite
{
    /// <summary>
    /// Writes <paramref name="content"/> so that <paramref name="filePath"/> only ever exists
    /// complete. On any failure the target is left exactly as it was and no temp file survives.
    /// </summary>
    /// <remarks><paramref name="content"/> is nullable to match
    /// <see cref="File.WriteAllTextAsync(string,string?,CancellationToken)"/>, which writes an
    /// empty file for <c>null</c>; callers that must never publish empty content check that
    /// themselves.</remarks>
    public static async Task WriteAllTextAsync(string filePath, string? content, CancellationToken ct)
    {
        var tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(tempPath, content, ct).ConfigureAwait(false);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }
}
