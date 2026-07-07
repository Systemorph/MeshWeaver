using System.Collections.Generic;
using MeshWeaver.Mesh;

namespace MeshWeaver.AI;

/// <summary>
/// The <c>manifest.json</c> at the root of a mesh export ZIP produced by
/// <see cref="MeshOperations.Export"/> and consumed by <see cref="MeshOperations.Import"/>.
///
/// <para>It carries the full JSON of every MeshNode in the exported subtree (node Content —
/// including Code source and Markdown bodies — rides inside each node) plus a listing of every
/// content-collection file whose raw bytes live under the ZIP's <c>files/</c> tree. Serialized
/// with the hub's JSON options so <c>$type</c> polymorphic node Content round-trips.</para>
/// </summary>
public record MeshExportManifest
{
    /// <summary>Manifest schema version (bump on breaking layout changes).</summary>
    public int Version { get; init; } = 1;

    /// <summary>The exported subtree's root path (e.g. <c>RtSrc</c>). Import rewrites this
    /// prefix to the target namespace.</summary>
    public string ExportRoot { get; init; } = string.Empty;

    /// <summary>UTC timestamp the export was produced.</summary>
    public DateTimeOffset ExportedAt { get; init; }

    /// <summary>Every MeshNode in the subtree (root + descendants), with full Content.</summary>
    public IReadOnlyList<MeshNode> Nodes { get; init; } = new List<MeshNode>();

    /// <summary>Every content-collection file exported; the raw bytes live at
    /// <c>files/{NodePath}/{Collection}/{FilePath}</c> inside the ZIP.</summary>
    public IReadOnlyList<MeshExportFileEntry> Files { get; init; } = new List<MeshExportFileEntry>();
}

/// <summary>
/// One content-collection file entry in a <see cref="MeshExportManifest"/>. The raw bytes are
/// stored in the ZIP at <c>files/{NodePath}/{Collection}/{FilePath}</c>.
/// </summary>
/// <param name="NodePath">The owning node's path (rewritten on import).</param>
/// <param name="Collection">The content-collection name on that node (e.g. <c>content</c>).</param>
/// <param name="FilePath">The file's collection-relative path (e.g. <c>hello.txt</c> or
/// <c>branding/logo.png</c>).</param>
/// <param name="Size">The file size in bytes (informational).</param>
public record MeshExportFileEntry(string NodePath, string Collection, string FilePath, int Size);
