using System;
using System.IO;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// The one way a node's <c>LastModified</c> is derived from a file on disk.
///
/// <para>🚨 <c>FileInfo.LastWriteTimeUtc</c> DOES NOT THROW for a file that is not there — it
/// returns <c>1601-01-01</c>, the FILETIME zero. Adopting that as a modification time stamps the
/// node with a value that is not a time, and because it is stable it then compares EQUAL to
/// itself across every subsequent edit. For a Code node that is fatal downstream: the per-source
/// version snapshots behind <c>NodeTypeDefinition.IsDirty</c> record 1601 on both sides, the
/// NodeType never recompiles again, and it serves its previous assembly while every status field
/// reads <c>Ok</c> (Systemorph/MeshWeaver#1836).</para>
///
/// <para>The exposure was per-FILE, not per-writer: within one import, paths that were stat-able
/// got real ticks and paths that were not got 1601 — which is why it looked like it tracked
/// GitSync. Three parsers and the filesystem adapter derived the stamp independently and only
/// <c>MarkdownFileParser</c> guarded; this helper exists so they cannot drift apart again.</para>
/// </summary>
public static class FileTimestamps
{
    /// <summary>
    /// The modification time to record for a node backed by <paramref name="filePath"/>: the
    /// file's real last-write time when it is actually on disk, else NOW — which is both honest
    /// (the node is being written at this moment) and, unlike 1601, comparable.
    /// </summary>
    public static DateTimeOffset ObservedAt(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        return fileInfo.Exists
            ? new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero)
            : DateTimeOffset.UtcNow;
    }
}
