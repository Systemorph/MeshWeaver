using System.Text;

namespace MeshWeaver.ContentCollections.Indexing;

/// <summary>
/// Deterministic, url-safe mesh-node path for a file's <c>Document</c> node. Lives in the
/// storage-agnostic indexing core (no <c>MeshWeaver.Graph</c> dependency) so both the indexing
/// service and the Graph-side <c>IDocumentSink</c> compute the SAME path for a given
/// <c>(collectionPath, filePath)</c> pair.
///
/// <para>The path is <c>{collectionPath}/_Documents/{slug(filePath)}</c>. It is a pure function of
/// its inputs: re-indexing the same file always targets the SAME node, so a content change updates
/// the existing <c>Document</c> rather than creating a duplicate. The slug keeps the original file
/// path readable (directory separators become a single segment-safe token) while guaranteeing every
/// character is url/path-safe — no <c>/</c>, no whitespace, no reserved punctuation that would
/// fork the path into extra mesh segments.</para>
/// </summary>
public static class DocumentPaths
{
    /// <summary>
    /// The reserved child namespace under a content collection where the per-file <c>Document</c>
    /// nodes live. Mirrors the <c>_Thread</c>/<c>_Access</c>/<c>_Activity</c> satellite-namespace
    /// convention (leading underscore = framework-reserved, not user content).
    /// </summary>
    public const string DocumentsSubNamespace = "_Documents";

    /// <summary>
    /// The deterministic node path for the <c>Document</c> describing <paramref name="filePath"/>
    /// within <paramref name="collectionPath"/>. Stable across re-indexes (same inputs → same path)
    /// and url-safe (no character that would split into extra path segments).
    /// </summary>
    public static string For(string collectionPath, string filePath)
    {
        if (string.IsNullOrWhiteSpace(collectionPath))
            throw new ArgumentException("Collection path is required.", nameof(collectionPath));
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        return $"{collectionPath.TrimEnd('/')}/{DocumentsSubNamespace}/{Slug(filePath)}";
    }

    /// <summary>
    /// Turns an arbitrary file path into a single deterministic, url-safe path segment. Letters,
    /// digits, <c>-</c>, <c>_</c> and <c>.</c> survive verbatim; every other character (directory
    /// separators, spaces, punctuation, non-ASCII) is replaced by <c>-</c>. Runs of replacements
    /// collapse to a single <c>-</c>, and leading/trailing <c>-</c> are trimmed, so the result is
    /// readable and never empty. Case is preserved (mesh paths are case-sensitive and two files that
    /// differ only by case are distinct files).
    /// </summary>
    public static string Slug(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        var sb = new StringBuilder(filePath.Length);
        var lastWasDash = false;
        foreach (var ch in filePath.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        // Guard the degenerate all-separator case (e.g. "///") so the path segment is never empty.
        return slug.Length == 0 ? "document" : slug;
    }
}
