using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;

namespace MeshWeaver.GitSync;

/// <summary>
/// Maps MeshNodes ↔ repo-relative file paths so an export and a re-import round-trip.
/// The convention mirrors the static-repo importer's
/// <c>MarkdownFileParser.DeriveIdAndNamespace</c> inverse:
/// <list type="bullet">
///   <item>the partition root (<c>{P}</c>) ↔ top-level <c>index.{ext}</c>;</item>
///   <item>a leaf node <c>{P}/A</c> ↔ <c>A.{ext}</c>;</item>
///   <item>a node <c>{P}/A</c> that has descendants ↔ <c>A/index.{ext}</c> (so its
///     children live in the <c>A/</c> folder);</item>
///   <item><c>{P}/A/B</c> ↔ <c>A/B.{ext}</c> (or <c>A/B/index.{ext}</c> if it too has children).</item>
/// </list>
/// The extension is chosen by the file-format parser that serializes the node
/// (Markdown → <c>.md</c>, typed/JSON → <c>.json</c>, code → <c>.cs</c>).
/// </summary>
public static class NodeFileMapper
{
    /// <summary>Path of a node relative to its partition root (empty for the root itself).</summary>
    public static string RelativePath(string path, string partition) =>
        string.Equals(path, partition, StringComparison.Ordinal) ? ""
        : path.StartsWith(partition + "/", StringComparison.Ordinal) ? path[(partition.Length + 1)..]
        : path;

    /// <summary>The repo-relative file path for a node (excludes the optional subdirectory prefix).</summary>
    public static string ToRepoPath(string nodePath, string partition, string ext, bool hasChildren)
    {
        var rel = RelativePath(nodePath, partition);
        if (rel.Length == 0) return $"index{ext}";
        return hasChildren ? $"{rel}/index{ext}" : $"{rel}{ext}";
    }

    /// <summary>The primary file extension the registry's serializer for this node uses (defaults to <c>.json</c>).</summary>
    public static string Extension(MeshNode node, FileFormatParserRegistry registry) =>
        registry.GetSerializerFor(node)?.SupportedExtensions.FirstOrDefault() ?? ".json";

    /// <summary>True when any other node in <paramref name="allPaths"/> lives under <paramref name="nodePath"/>.</summary>
    public static bool HasChildren(string nodePath, IEnumerable<string> allPaths)
    {
        var prefix = nodePath + "/";
        return allPaths.Any(p => p.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>True when a repo-relative path is the top-level <c>index.*</c> (i.e. the partition root).</summary>
    public static bool IsRootIndex(string relativePath)
    {
        var p = StripExtension(relativePath.Replace('\\', '/').Trim('/'));
        return p.Equals("index", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Derives (id, relative-namespace) from a repo-relative file path — the inverse
    /// of <see cref="ToRepoPath"/>. <c>A/B.md</c> → (B, A); <c>A/index.md</c> → (A, "");
    /// <c>A.md</c> → (A, "").
    /// </summary>
    public static (string Id, string Namespace) FromRelativePath(string relativePath)
    {
        var p = StripExtension(relativePath.Replace('\\', '/').Trim('/'));
        var lastSlash = p.LastIndexOf('/');
        if (lastSlash < 0) return (p, "");
        var ns = p[..lastSlash];
        var id = p[(lastSlash + 1)..];
        if (id.Equals("index", StringComparison.OrdinalIgnoreCase))
        {
            var parentSlash = ns.LastIndexOf('/');
            return parentSlash < 0 ? (ns, "") : (ns[(parentSlash + 1)..], ns[..parentSlash]);
        }
        return (id, ns);
    }

    private static string StripExtension(string p)
    {
        var lastSlash = p.LastIndexOf('/');
        var dot = p.LastIndexOf('.');
        return dot > lastSlash ? p[..dot] : p;
    }
}
