using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Generates C# script code that can be evaluated by CSharpScript to return a MeshNode.
/// Unlike DynamicMeshNodeAttributeGenerator, this generates script code (not class definitions)
/// that directly returns a MeshNode when evaluated.
/// </summary>
internal class ScriptCodeGenerator
{
    /// <summary>
    /// Generates C# script code that returns a MeshNode when evaluated.
    /// </summary>
    /// <param name="node">The MeshNode being compiled.</param>
    /// <param name="codeFile">The CodeConfiguration containing user code.</param>
    /// <param name="hubConfiguration">The HubConfiguration lambda expression (from NodeTypeDefinition).</param>
    /// <param name="contentCollections">Content collections to register for this node type.</param>
    /// <returns>C# script code that returns a MeshNode when evaluated by CSharpScript.</returns>
    public string GenerateScriptSource(
        MeshNode node,
        CodeConfiguration? codeFile,
        string? hubConfiguration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections = null)
    {
        var code = codeFile?.Code;
        var hasCode = !string.IsNullOrWhiteSpace(code);

        var sb = new StringBuilder();

        // Header comment
        sb.AppendLine($"// Auto-generated script for MeshNode: {node.Path}");
        sb.AppendLine($"// Generated at: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine("// This script returns a MeshNode when evaluated by CSharpScript");
        sb.AppendLine();

        // User code directly at top level (types, classes, etc.)
        if (hasCode)
        {
            sb.AppendLine("// User-defined types");
            sb.AppendLine(code!);
            sb.AppendLine();
        }

        // Generate the ConfigureHub local function
        sb.AppendLine("// Hub configuration function");
        sb.AppendLine("MessageHubConfiguration ConfigureHub(MessageHubConfiguration config)");
        sb.AppendLine("{");
        sb.AppendLine("    var result = config;");
        sb.AppendLine();

        // Add MeshDataSource for data access
        sb.AppendLine("    // Add data source and default views");
        sb.AppendLine("    result = result.AddMeshDataSource();");
        sb.AppendLine();

        // Add content collections if configured
        if (contentCollections is { Count: > 0 })
        {
            sb.AppendLine("    // Register content collections");
            sb.AppendLine("    result = result.AddContentCollections(");

            for (var i = 0; i < contentCollections.Count; i++)
            {
                var collection = contentCollections[i];
                var comma = i < contentCollections.Count - 1 ? "," : "";

                sb.AppendLine("        new ContentCollectionConfig");
                sb.AppendLine("        {");
                sb.AppendLine($"            Name = \"{EscapeString(collection.Name)}\",");
                sb.AppendLine($"            SourceType = \"{EscapeString(collection.SourceType)}\",");

                if (!string.IsNullOrEmpty(collection.DisplayName))
                    sb.AppendLine($"            DisplayName = \"{EscapeString(collection.DisplayName)}\",");

                if (!string.IsNullOrEmpty(collection.BasePath))
                    sb.AppendLine($"            BasePath = \"{EscapeString(collection.BasePath)}\",");

                if (collection.Order != 0)
                    sb.AppendLine($"            Order = {collection.Order},");

                if (collection.Settings is { Count: > 0 })
                {
                    sb.AppendLine("            Settings = new Dictionary<string, string>");
                    sb.AppendLine("            {");
                    foreach (var kvp in collection.Settings)
                    {
                        sb.AppendLine($"                [\"{EscapeString(kvp.Key)}\"] = \"{EscapeString(kvp.Value)}\",");
                    }
                    sb.AppendLine("            },");
                }

                sb.AppendLine($"        }}{comma}");
            }

            sb.AppendLine("    );");
            sb.AppendLine();
        }

        // Apply user's HubConfiguration lambda if provided
        if (!string.IsNullOrWhiteSpace(hubConfiguration))
        {
            sb.AppendLine("    // Apply user's HubConfiguration lambda");
            sb.AppendLine($"    Func<MessageHubConfiguration, MessageHubConfiguration> userConfig = {hubConfiguration};");
            sb.AppendLine("    result = userConfig(result);");
            sb.AppendLine();
        }

        sb.AppendLine("    return result;");
        sb.AppendLine("}");
        sb.AppendLine();

        // Return the MeshNode directly - this is what CSharpScript.EvaluateAsync will return
        sb.AppendLine("// Return the MeshNode");
        sb.AppendLine($"new MeshNode(\"{EscapeString(node.Path)}\")");
        sb.AppendLine("{");
        sb.AppendLine($"    Name = \"{EscapeString(node.Name)}\",");
        sb.AppendLine($"    NodeType = \"{EscapeString(node.NodeType)}\",");
        sb.AppendLine($"    Description = \"{EscapeString(node.Description)}\",");
        sb.AppendLine($"    Icon = \"{EscapeString(node.Icon)}\",");
        sb.AppendLine($"    DisplayOrder = {(node.DisplayOrder.HasValue ? node.DisplayOrder.Value.ToString() : "null")},");
        sb.AppendLine($"    LastModified = DateTimeOffset.Parse(\"{node.LastModified:O}\"),");
        sb.AppendLine("    HubConfiguration = ConfigureHub");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Sanitizes a node path to a valid C# identifier.
    /// </summary>
    public string SanitizeName(string nodePath)
    {
        // Replace invalid characters
        var sanitized = Regex.Replace(nodePath, @"[^a-zA-Z0-9]", "_");

        // Remove consecutive underscores
        while (sanitized.Contains("__"))
            sanitized = sanitized.Replace("__", "_");

        sanitized = sanitized.Trim('_');

        // Ensure it starts with a letter
        if (sanitized.Length > 0 && !char.IsLetter(sanitized[0]))
            sanitized = "Node_" + sanitized;

        // Ensure we have a valid identifier
        if (string.IsNullOrEmpty(sanitized))
            sanitized = "DynamicNode";

        return sanitized;
    }

    /// <summary>
    /// Escapes a string for use in C# string literals.
    /// </summary>
    private static string EscapeString(string? value)
    {
        if (value == null) return "";

        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}
