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

        // Extract using statements from user code (they must go at the top)
        var (userUsings, userCodeWithoutUsings) = ExtractUsingStatements(code);

        var sb = new StringBuilder();

        // Header comment
        sb.AppendLine($"// Auto-generated from MeshNode: {node.Path}");
        sb.AppendLine($"// Generated at: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine("// Source file for debugging support - do not edit manually");
        sb.AppendLine();

        // Using statements (standard ones)
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.ComponentModel;");
        sb.AppendLine("using System.ComponentModel.DataAnnotations;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Reactive.Linq;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine("using MeshWeaver.Mesh;");
        sb.AppendLine("using MeshWeaver.Messaging;");
        sb.AppendLine("using MeshWeaver.Data;");
        sb.AppendLine("using MeshWeaver.Domain;");
        sb.AppendLine("using MeshWeaver.Graph;");
        sb.AppendLine("using MeshWeaver.Graph.Configuration;");
        sb.AppendLine("using MeshWeaver.Layout;");
        sb.AppendLine("using MeshWeaver.Layout.Composition;");
        sb.AppendLine("using MeshWeaver.Layout.Domain;");
        sb.AppendLine("using MeshWeaver.Layout.Views;");
        sb.AppendLine("using MeshWeaver.Application.Styles;");
        sb.AppendLine("using MeshWeaver.ContentCollections;");
        sb.AppendLine("using MeshWeaver.Mesh.Services;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.Configuration;");

        // User-defined using statements (extracted from code files)
        foreach (var userUsing in userUsings)
        {
            sb.AppendLine(userUsing);
        }
        sb.AppendLine();

        // Assembly attribute - MUST come before any namespace declarations
        sb.AppendLine($"[assembly: MeshWeaver.Graph.Generated.{safeClassName}MeshNode]");
        sb.AppendLine();

        // User code directly (no namespace wrapper so types are accessible by simple name)
        if (!string.IsNullOrWhiteSpace(userCodeWithoutUsings))
        {
            sb.AppendLine("// User-defined types");
            sb.AppendLine(userCodeWithoutUsings);
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
        sb.AppendLine($"                Icon = \"{EscapeString(node.Icon)}\",");
        sb.AppendLine($"                DisplayOrder = {(node.DisplayOrder.HasValue ? node.DisplayOrder.Value.ToString() : "null")},");
        sb.AppendLine($"                LastModified = DateTimeOffset.Parse(\"{node.LastModified:O}\"),");
        sb.AppendLine($"                AssemblyLocation = typeof({safeClassName}MeshNodeAttribute).Assembly.Location,");
        sb.AppendLine("                HubConfiguration = ConfigureHub");
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


        // Add MeshDataSource for data access
        sb.AppendLine("            // Add data source and default views");
        sb.AppendLine("            result = result.AddMeshDataSource();");
        sb.AppendLine();

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
    /// Extracts using statements from user code and returns them separately.
    /// Using statements must be at the top of the generated file.
    /// </summary>
    private static (List<string> Usings, string CodeWithoutUsings) ExtractUsingStatements(string? code)
    {
        var usings = new List<string>();

        if (string.IsNullOrWhiteSpace(code))
            return (usings, code ?? "");

        var lines = code.Split('\n');
        var codeLines = new List<string>();
        var inCommentBlock = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            // Track multi-line comments
            if (trimmed.StartsWith("/*"))
                inCommentBlock = true;
            if (trimmed.EndsWith("*/"))
            {
                inCommentBlock = false;
                codeLines.Add(line);
                continue;
            }

            if (inCommentBlock)
            {
                codeLines.Add(line);
                continue;
            }

            // Check if this is a using statement (not inside a class/struct/etc)
            if (trimmed.StartsWith("using ") && trimmed.EndsWith(";") && !trimmed.Contains("("))
            {
                // This is a using directive, extract it
                usings.Add(line);
            }
            else
            {
                codeLines.Add(line);
            }
        }

        return (usings, string.Join("\n", codeLines));
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
