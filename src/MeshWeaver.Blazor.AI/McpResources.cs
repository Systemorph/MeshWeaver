using System.ComponentModel;
using MeshWeaver.AI;
using ModelContextProtocol.Server;

namespace MeshWeaver.Blazor.AI;

/// <summary>
/// MCP resources: reference documentation an MCP client reads once instead of
/// rediscovering syntax through trial-and-error tool calls. Served from the same
/// embedded ToolsReference document the in-portal agents get @@-included into
/// their instructions, so both surfaces stay in sync by construction.
/// </summary>
[McpServerResourceType]
public class McpResources
{
    [McpServerResource(UriTemplate = "meshweaver://reference/tools", Name = "tools-reference",
        Title = "MeshWeaver tools reference", MimeType = "text/markdown")]
    [Description("Complete reference for the mesh tools: @-path resolution rules, GitHub-style Search query syntax, MeshNode schema for create/update, unified path prefixes (data/, schema/, content/, area/), content collections, satellite namespaces, and icon rules.")]
    public static string ToolsReference()
    {
        var assembly = typeof(BuiltInAgentProvider).Assembly;
        const string resourceName = "MeshWeaver.AI.Data.Agent.ToolsReference.md";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return $"(embedded resource {resourceName} not found)";
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        // Strip the YAML frontmatter — provider metadata, not reference content.
        if (content.StartsWith("---"))
        {
            var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
                content = content[(end + 4)..].TrimStart('\r', '\n');
        }
        return content;
    }
}
