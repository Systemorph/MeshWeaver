using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Generates C# source code for dynamic MeshNodeAttribute classes.
/// The generated attribute includes any user code and HubConfiguration.
/// </summary>
internal class DynamicMeshNodeAttributeGenerator
{
    /// <summary>
    /// Generates the complete C# source code for a dynamic node assembly.
    /// </summary>
    /// <param name="node">The MeshNode being compiled.</param>
    /// <param name="codeFile">The CodeConfiguration containing user code.</param>
    /// <param name="hubConfiguration">The HubConfiguration lambda expression (from NodeTypeDefinition).</param>
    /// <param name="contentCollections">Content collections to register for this node type.</param>
    /// <returns>Complete C# source code ready for compilation.</returns>
    public string GenerateAttributeSource(
        MeshNode node,
        CodeConfiguration? codeFile,
        string? hubConfiguration,
        IReadOnlyList<ContentCollectionConfig>? contentCollections = null)
    {
        var safeClassName = SanitizeName(node.Path);
        var code = codeFile?.Code;
        var hasCode = !string.IsNullOrWhiteSpace(code);

        var sb = new StringBuilder();

        // Header comment
        sb.AppendLine($"// Auto-generated from MeshNode: {node.Path}");
        sb.AppendLine($"// Generated at: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine("// Source file for debugging support - do not edit manually");
        sb.AppendLine();

        // Using statements
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.ComponentModel.DataAnnotations;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Reactive.Linq;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine("using MeshWeaver.Mesh;");
        sb.AppendLine("using MeshWeaver.Messaging;");
        sb.AppendLine("using MeshWeaver.Data;");
        sb.AppendLine("using MeshWeaver.Graph;");
        sb.AppendLine("using MeshWeaver.Graph.Configuration;");
        sb.AppendLine("using MeshWeaver.ContentCollections;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.Configuration;");
        sb.AppendLine("using Code;");
        sb.AppendLine();

        // Assembly attribute - MUST come before any namespace declarations
        sb.AppendLine($"[assembly: MeshWeaver.Graph.Generated.{safeClassName}MeshNode]");
        sb.AppendLine();

        // User code in Code namespace (block-scoped)
        // Note: Global namespace causes Roslyn emit bugs, so we use a minimal namespace
        if (hasCode)
        {
            sb.AppendLine("namespace Code");
            sb.AppendLine("{");
            var indentedCode = IndentCode(code!, "    ");
            sb.AppendLine(indentedCode);
            sb.AppendLine("}");
            sb.AppendLine();
        }

        // Generated namespace
        sb.AppendLine("namespace MeshWeaver.Graph.Generated");
        sb.AppendLine("{");

        // MeshNodeAttribute class
        sb.AppendLine($"    public class {safeClassName}MeshNodeAttribute : MeshNodeAttribute");
        sb.AppendLine("    {");

        // Nodes property
        sb.AppendLine("        public override IEnumerable<MeshNode> Nodes =>");
        sb.AppendLine("        [");
        sb.AppendLine($"            new MeshNode(\"{EscapeString(node.Path)}\")");
        sb.AppendLine("            {");
        sb.AppendLine($"                Name = \"{EscapeString(node.Name)}\",");
        sb.AppendLine($"                NodeType = \"{EscapeString(node.NodeType)}\",");
        sb.AppendLine($"                Description = \"{EscapeString(node.Description)}\",");
        sb.AppendLine($"                IconName = \"{EscapeString(node.IconName)}\",");
        sb.AppendLine($"                DisplayOrder = {node.DisplayOrder},");
        sb.AppendLine($"                IsPersistent = {node.IsPersistent.ToString().ToLowerInvariant()},");
        sb.AppendLine($"                LastModified = DateTimeOffset.Parse(\"{node.LastModified:O}\"),");
        sb.AppendLine($"                AssemblyLocation = typeof({safeClassName}MeshNodeAttribute).Assembly.Location,");
        sb.AppendLine("                HubConfiguration = Observable.Return<Func<MessageHubConfiguration, MessageHubConfiguration>?>(ConfigureHub)");
        sb.AppendLine("            }");
        sb.AppendLine("        ];");
        sb.AppendLine();

        // ConfigureHub method
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Hub configuration - applies default views, then applies user's HubConfiguration.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        private static MessageHubConfiguration ConfigureHub(MessageHubConfiguration config)");
        sb.AppendLine("        {");
        sb.AppendLine("            var result = config;");
        sb.AppendLine();


        // For NodeType definitions (content is NodeTypeDefinition), use MeshHubBuilder with CodeConfiguration
        // Check Content type, not NodeType property, because NodeType is set to the path for the generated config
        // Always add WithDefaultViews() so instances of this type get standard views (Details, Thumbnail, etc.)
        if (node.Content is NodeTypeDefinition)
        {
            sb.AppendLine("            // For NodeType definitions, use MeshDataSource with CodeConfiguration + default views");
            sb.AppendLine("            result = result.ConfigureMeshHub().WithCodeConfiguration().Build().WithDefaultViews();");
            sb.AppendLine();
        }
        else
        {
            // For non-NodeType nodes (instance nodes), add MeshDataSource and default views
            sb.AppendLine("            // For non-NodeType nodes, add data source and default views");
            sb.AppendLine("            result = result.AddMeshDataSource().WithDefaultViews();");
            sb.AppendLine();
        }

        // Add content collections if configured
        if (contentCollections is { Count: > 0 })
        {
            sb.AppendLine("            // Register content collections");
            sb.AppendLine("            result = result.AddContentCollections(");

            for (var i = 0; i < contentCollections.Count; i++)
            {
                var collection = contentCollections[i];
                var comma = i < contentCollections.Count - 1 ? "," : "";

                sb.AppendLine($"                new ContentCollectionConfig");
                sb.AppendLine("                {");
                sb.AppendLine($"                    Name = \"{EscapeString(collection.Name)}\",");
                sb.AppendLine($"                    SourceType = \"{EscapeString(collection.SourceType)}\",");

                if (!string.IsNullOrEmpty(collection.DisplayName))
                    sb.AppendLine($"                    DisplayName = \"{EscapeString(collection.DisplayName)}\",");

                if (!string.IsNullOrEmpty(collection.BasePath))
                    sb.AppendLine($"                    BasePath = \"{EscapeString(collection.BasePath)}\",");

                if (collection.Order != 0)
                    sb.AppendLine($"                    Order = {collection.Order},");

                if (collection.Settings is { Count: > 0 })
                {
                    sb.AppendLine("                    Settings = new Dictionary<string, string>");
                    sb.AppendLine("                    {");
                    foreach (var kvp in collection.Settings)
                    {
                        sb.AppendLine($"                        [\"{EscapeString(kvp.Key)}\"] = \"{EscapeString(kvp.Value)}\",");
                    }
                    sb.AppendLine("                    },");
                }

                sb.AppendLine($"                }}{comma}");
            }

            sb.AppendLine("            );");
            sb.AppendLine();
        }

        // Apply user's HubConfiguration lambda if provided
        if (!string.IsNullOrWhiteSpace(hubConfiguration))
        {
            sb.AppendLine("            // Apply user's HubConfiguration lambda");
            sb.AppendLine($"            Func<MessageHubConfiguration, MessageHubConfiguration> userConfig = {hubConfiguration};");
            sb.AppendLine("            result = userConfig(result);");
            sb.AppendLine();
        }

        sb.AppendLine("            return result;");
        sb.AppendLine("        }");

        sb.AppendLine("    }");
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

    /// <summary>
    /// Indents code with the specified prefix.
    /// </summary>
    private static string IndentCode(string code, string indent)
    {
        var lines = code.Split('\n');
        return string.Join("\n", lines.Select(line => indent + line.TrimEnd('\r')));
    }
}
