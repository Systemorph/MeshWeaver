using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Generates C# source code for dynamic MeshNodeAttribute classes.
/// The generated attribute includes the DataModel type, MeshNode configuration,
/// and HubConfiguration following the same pattern as NodeTypeRegistrationInitializer.
/// </summary>
internal class DynamicMeshNodeAttributeGenerator
{
    /// <summary>
    /// Generates the complete C# source code for a dynamic node assembly.
    /// Includes the DataModel type, MeshNodeAttribute, and HubConfiguration.
    /// </summary>
    /// <param name="node">The MeshNode being compiled.</param>
    /// <param name="dataModel">The DataModel containing the TypeSource.</param>
    /// <param name="nodeTypeConfig">Optional NodeTypeConfig with additional settings.</param>
    /// <param name="hubFeatures">Optional HubFeatureConfig for hub configuration.</param>
    /// <returns>Complete C# source code ready for compilation.</returns>
    public string GenerateAttributeSource(
        MeshNode node,
        DataModel dataModel,
        NodeTypeConfig? nodeTypeConfig,
        HubFeatureConfig? hubFeatures)
    {
        var safeClassName = SanitizeName(node.Path);
        var typeName = ExtractTypeName(dataModel.TypeSource);
        var enableDynamicAreas = hubFeatures?.EnableDynamicNodeTypeAreas ?? true;
        var contentCollectionsCode = GenerateContentCollectionsCode(nodeTypeConfig?.ContentCollections);

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
        sb.AppendLine("using MeshWeaver.Graph.Configuration;");
        sb.AppendLine("using MeshWeaver.ContentCollections;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.Configuration;");
        sb.AppendLine();

        // Assembly attribute - MUST come before any namespace declarations
        sb.AppendLine($"[assembly: MeshWeaver.Graph.Generated.{safeClassName}MeshNode]");
        sb.AppendLine();

        // DataModel type in Dynamic namespace
        sb.AppendLine("namespace MeshWeaver.Graph.Dynamic");
        sb.AppendLine("{");
        sb.AppendLine("    // DataModel TypeSource");
        // Indent the TypeSource
        var indentedTypeSource = IndentCode(dataModel.TypeSource, "    ");
        sb.AppendLine(indentedTypeSource);
        sb.AppendLine("}");
        sb.AppendLine();

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
        sb.AppendLine($"                NodeType = \"{EscapeString(nodeTypeConfig?.NodeType ?? node.NodeType)}\",");
        sb.AppendLine($"                DataType = typeof(MeshWeaver.Graph.Dynamic.{typeName}),");
        sb.AppendLine("                HubConfiguration = ConfigureHub,");
        sb.AppendLine($"                DisplayName = \"{EscapeString(nodeTypeConfig?.DisplayName ?? dataModel.DisplayName)}\",");
        sb.AppendLine($"                Description = \"{EscapeString(nodeTypeConfig?.Description ?? dataModel.Description)}\",");
        sb.AppendLine($"                IconName = \"{EscapeString(nodeTypeConfig?.IconName ?? dataModel.IconName)}\",");
        sb.AppendLine($"                DisplayOrder = {nodeTypeConfig?.DisplayOrder ?? dataModel.DisplayOrder}");
        sb.AppendLine("            }");
        sb.AppendLine("        ];");
        sb.AppendLine();

        // ConfigureHub method
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Hub configuration - same pattern as NodeTypeRegistrationInitializer.ConfigureHub()");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        private static MessageHubConfiguration ConfigureHub(MessageHubConfiguration config)");
        sb.AppendLine("        {");
        sb.AppendLine("            var builder = config.ConfigureMeshHub();");
        sb.AppendLine($"            builder = builder.WithDataType(typeof(MeshWeaver.Graph.Dynamic.{typeName}));");
        sb.AppendLine("            var result = builder.Build();");
        sb.AppendLine();

        if (enableDynamicAreas)
        {
            sb.AppendLine("            result = result.AddDynamicNodeTypeAreas();");
        }
        else
        {
            sb.AppendLine("            // Dynamic areas disabled");
        }

        sb.AppendLine();
        sb.AppendLine(contentCollectionsCode);
        sb.AppendLine();
        sb.AppendLine("            return result;");
        sb.AppendLine("        }");

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Generates code for adding content collections to the hub configuration.
    /// </summary>
    private string GenerateContentCollectionsCode(List<ContentCollectionMapping>? collections)
    {
        if (collections == null || collections.Count == 0)
            return "            // No content collections configured";

        var sb = new StringBuilder();
        foreach (var mapping in collections)
        {
            sb.AppendLine($"            result = result.AddContentCollection(sp =>");
            sb.AppendLine("            {");
            sb.AppendLine("                var appConfig = sp.GetService<IConfiguration>();");
            sb.AppendLine("                var storageProvider = appConfig?.GetSection(\"Graph\")[\"StorageProvider\"] ?? \"FileSystem\";");
            sb.AppendLine($"                var resolvedSubPath = \"{EscapeString(mapping.SubPath)}\".Replace(\"{{id}}\", config.Address.Id);");
            sb.AppendLine();
            sb.AppendLine("                if (storageProvider.Equals(\"AzureBlob\", StringComparison.OrdinalIgnoreCase))");
            sb.AppendLine("                {");
            sb.AppendLine("                    var containerName = appConfig?.GetSection(\"Graph\")[\"ContainerName\"] ?? \"graph\";");
            sb.AppendLine("                    return new ContentCollectionConfig");
            sb.AppendLine("                    {");
            sb.AppendLine($"                        Name = \"{EscapeString(mapping.Name)}\",");
            sb.AppendLine("                        SourceType = \"AzureBlob\",");
            sb.AppendLine("                        BasePath = resolvedSubPath,");
            sb.AppendLine("                        Settings = new Dictionary<string, string>");
            sb.AppendLine("                        {");
            sb.AppendLine("                            [\"ContainerName\"] = containerName,");
            sb.AppendLine("                            [\"ClientName\"] = \"default\"");
            sb.AppendLine("                        }");
            sb.AppendLine("                    };");
            sb.AppendLine("                }");
            sb.AppendLine("                else");
            sb.AppendLine("                {");
            sb.AppendLine("                    var dataDirectory = appConfig?.GetSection(\"Graph\")[\"DataDirectory\"] ?? \"Data\";");
            sb.AppendLine("                    return new ContentCollectionConfig");
            sb.AppendLine("                    {");
            sb.AppendLine($"                        Name = \"{EscapeString(mapping.Name)}\",");
            sb.AppendLine("                        SourceType = \"FileSystem\",");
            sb.AppendLine("                        BasePath = Path.Combine(dataDirectory, resolvedSubPath)");
            sb.AppendLine("                    };");
            sb.AppendLine("                }");
            sb.AppendLine("            });");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Extracts the primary type name from TypeSource code.
    /// Looks for public record/class declarations.
    /// </summary>
    public string ExtractTypeName(string typeSource)
    {
        // Match: public record TypeName or public class TypeName
        var match = Regex.Match(
            typeSource,
            @"public\s+(?:sealed\s+)?(?:record|class)\s+(\w+)",
            RegexOptions.Singleline);

        if (match.Success)
            return match.Groups[1].Value;

        // Fallback: look for any record/class declaration
        match = Regex.Match(
            typeSource,
            @"(?:record|class)\s+(\w+)",
            RegexOptions.Singleline);

        return match.Success ? match.Groups[1].Value : "DynamicType";
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
