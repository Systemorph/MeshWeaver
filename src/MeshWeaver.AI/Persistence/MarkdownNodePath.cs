namespace MeshWeaver.AI.Persistence;

/// <summary>
/// The ONE relative-path → (id, namespace) rule shared by the AI module's two contributed
/// <c>.md</c> parsers (<see cref="AgentFileParser"/> and <see cref="SkillFileParser"/>).
///
/// <para>It matches the convention the catch-all <c>MarkdownFileParser</c> applies, which is what
/// makes a file land at the same path whichever parser in the chain claims it: the directory chain
/// is the namespace, the file name (without extension) is the id, and <c>index.md</c> names the
/// PARENT directory node rather than a child called "index".</para>
///
/// <para>Kept in one place because the two parsers must agree. When they did not, the symptom was
/// not an error — it was a node quietly appearing at a second path.</para>
/// </summary>
internal static class MarkdownNodePath
{
    /// <summary>Splits a <c>.md</c> relative path into the node's id and its namespace (null at root).</summary>
    /// <param name="relativePath">Path relative to the data root, e.g. <c>Hosting/Skill/deployment.md</c>.</param>
    internal static (string Id, string? Namespace) DeriveIdAndNamespace(string relativePath)
    {
        // Remove extension and normalize
        var pathWithoutExt = relativePath;
        if (pathWithoutExt.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            pathWithoutExt = pathWithoutExt[..^3];

        pathWithoutExt = pathWithoutExt.Trim('/').Replace('\\', '/');

        var lastSlash = pathWithoutExt.LastIndexOf('/');
        if (lastSlash < 0)
            return (pathWithoutExt, null);

        var ns = pathWithoutExt[..lastSlash];
        var id = pathWithoutExt[(lastSlash + 1)..];

        // index.md represents the parent directory node, not a child called "index"
        if (id.Equals("index", StringComparison.OrdinalIgnoreCase))
        {
            var parentSlash = ns.LastIndexOf('/');
            if (parentSlash < 0)
                return (ns, null);
            return (ns[(parentSlash + 1)..], ns[..parentSlash]);
        }

        return (id, ns);
    }
}
