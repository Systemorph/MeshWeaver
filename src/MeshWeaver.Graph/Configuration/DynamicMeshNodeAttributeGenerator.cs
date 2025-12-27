using System.Text;
using System.Text.RegularExpressions;
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
    /// <param name="codeConfig">The CodeConfiguration containing user code.</param>
    /// <param name="hubConfiguration">The HubConfiguration lambda expression (from NodeTypeDefinition).</param>
    /// <returns>Complete C# source code ready for compilation.</returns>
    public string GenerateAttributeSource(
        MeshNode node,
        CodeConfiguration? codeConfig,
        string? hubConfiguration)
    {
        var safeClassName = SanitizeName(node.Path);
        // Use GetCombinedCode() for multi-file support
        var combinedCode = codeConfig?.GetCombinedCode();
        var hasCode = !string.IsNullOrWhiteSpace(combinedCode);

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
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine("using MeshWeaver.Mesh;");
        sb.AppendLine("using MeshWeaver.Messaging;");
        sb.AppendLine("using MeshWeaver.Data;");
        sb.AppendLine("using MeshWeaver.Graph;");
        sb.AppendLine("using MeshWeaver.Graph.Configuration;");
        sb.AppendLine("using MeshWeaver.ContentCollections;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.Configuration;");
        sb.AppendLine();

        // Assembly attribute - MUST come before any namespace declarations
        sb.AppendLine($"[assembly: MeshWeaver.Graph.Generated.{safeClassName}MeshNode]");
        sb.AppendLine();

        // User code in Dynamic namespace (if any)
        if (hasCode)
        {
            sb.AppendLine("namespace MeshWeaver.Graph.Dynamic");
            sb.AppendLine("{");
            var indentedCode = IndentCode(combinedCode!, "    ");
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
        sb.AppendLine("                HubConfiguration = ConfigureHub");
        sb.AppendLine("            }");
        sb.AppendLine("        ];");
        sb.AppendLine();

        // NodeTypeConfigurations property
        sb.AppendLine("        public override IEnumerable<NodeTypeConfiguration> NodeTypeConfigurations =>");
        sb.AppendLine("        [");
        sb.AppendLine("            new NodeTypeConfiguration");
        sb.AppendLine("            {");
        sb.AppendLine($"                NodeType = \"{EscapeString(node.NodeType)}\",");
        sb.AppendLine("                DataType = typeof(object),");
        sb.AppendLine("                HubConfiguration = ConfigureHub,");
        sb.AppendLine($"                DisplayName = \"{EscapeString(node.Name)}\",");
        sb.AppendLine($"                Description = \"{EscapeString(node.Description)}\",");
        sb.AppendLine($"                IconName = \"{EscapeString(node.IconName)}\",");
        sb.AppendLine($"                DisplayOrder = {node.DisplayOrder}");
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

        // Only add default views for non-NodeType nodes
        // NodeType nodes get their views from BuiltInNodeTypes.cs (AddNodeTypeView)
        if (node.NodeType != BuiltInNodeTypes.NodeTypeId)
        {
            sb.AppendLine("            // Add default views (Details, Edit, Thumbnail, Metadata, Settings, Comments)");
            sb.AppendLine("            result = result.WithDefaultViews();");
            sb.AppendLine();
        }
        sb.AppendLine("            // Add dynamic node type areas");
        sb.AppendLine("            result = result.AddDynamicNodeTypeAreas();");
        sb.AppendLine();

        // For NodeType nodes, add CodeConfiguration as accessible data
        if (node.NodeType == "NodeType")
        {
            sb.AppendLine("            // For NodeType nodes, add CodeConfiguration as accessible data");
            sb.AppendLine("            result = result.AddData(data =>");
            sb.AppendLine("            {");
            sb.AppendLine("                var persistence = data.Workspace.Hub.ServiceProvider.GetService<MeshWeaver.Mesh.Services.IPersistenceService>();");
            sb.AppendLine("                var hubPath = data.Workspace.Hub.Address.ToString();");
            sb.AppendLine("                if (persistence != null)");
            sb.AppendLine("                {");
            sb.AppendLine("                    return data.AddSource(source => source.WithTypeSource(typeof(CodeConfiguration),");
            sb.AppendLine("                        new CodeConfigurationTypeSource(data.Workspace, source.Id, persistence, hubPath)));");
            sb.AppendLine("                }");
            sb.AppendLine("                return data.AddSource(source => source.WithType<CodeConfiguration>(ts => ts.WithKey(_ => \"code\")));");
            sb.AppendLine("            });");
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
