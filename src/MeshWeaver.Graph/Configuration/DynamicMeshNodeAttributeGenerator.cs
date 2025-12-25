using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Generates C# source code for dynamic MeshNodeAttribute classes.
/// The generated attribute includes the DataModel type, MeshNode configuration,
/// and HubConfiguration following the same pattern as NodeTypeRegistrationInitializer.
/// Supports multiple DataModels and LayoutAreas per type node.
/// </summary>
internal class DynamicMeshNodeAttributeGenerator
{
    /// <summary>
    /// Generates the complete C# source code for a dynamic node assembly.
    /// Supports multiple DataModels and LayoutAreas.
    /// </summary>
    /// <param name="node">The MeshNode being compiled.</param>
    /// <param name="dataModels">The DataModels containing type sources (can be empty).</param>
    /// <param name="layoutAreas">The LayoutAreaConfigs for this type.</param>
    /// <param name="nodeTypeConfig">Optional NodeTypeConfig with additional settings.</param>
    /// <param name="hubFeatures">Optional HubFeatureConfig for hub configuration.</param>
    /// <returns>Complete C# source code ready for compilation.</returns>
    public string GenerateAttributeSource(
        MeshNode node,
        IReadOnlyList<DataModel> dataModels,
        IReadOnlyList<LayoutAreaConfig> layoutAreas,
        NodeTypeConfig? nodeTypeConfig,
        HubFeatureConfig? hubFeatures)
    {
        var safeClassName = SanitizeName(node.Path);
        var enableDynamicAreas = hubFeatures?.EnableDynamicNodeTypeAreas ?? true;
        var contentCollectionsCode = GenerateContentCollectionsCode(nodeTypeConfig?.ContentCollections);

        var sb = new StringBuilder();

        // Header comment
        sb.AppendLine($"// Auto-generated from MeshNode: {node.Path}");
        sb.AppendLine($"// Generated at: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"// DataModels: {dataModels.Count}, LayoutAreas: {layoutAreas.Count}");
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

        // DataModel types in Dynamic namespace
        if (dataModels.Count > 0)
        {
            sb.AppendLine("namespace MeshWeaver.Graph.Dynamic");
            sb.AppendLine("{");
            foreach (var dataModel in dataModels)
            {
                sb.AppendLine($"    // DataModel: {dataModel.Id}");
                var indentedTypeSource = IndentCode(dataModel.TypeSource, "    ");
                sb.AppendLine(indentedTypeSource);
                sb.AppendLine();
            }
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

        // NodeTypeConfigurations property - one per DataModel
        sb.AppendLine("        public override IEnumerable<NodeTypeConfiguration> NodeTypeConfigurations =>");
        sb.AppendLine("        [");
        if (dataModels.Count > 0)
        {
            for (var i = 0; i < dataModels.Count; i++)
            {
                var dataModel = dataModels[i];
                var typeName = ExtractTypeName(dataModel.TypeSource);
                var isLast = i == dataModels.Count - 1;

                sb.AppendLine("            new NodeTypeConfiguration");
                sb.AppendLine("            {");
                sb.AppendLine($"                NodeType = \"{EscapeString(nodeTypeConfig?.NodeType ?? node.NodeType)}\",");
                sb.AppendLine($"                DataType = typeof(MeshWeaver.Graph.Dynamic.{typeName}),");
                sb.AppendLine("                HubConfiguration = ConfigureHub,");
                sb.AppendLine($"                DisplayName = \"{EscapeString(nodeTypeConfig?.DisplayName ?? dataModel.DisplayName)}\",");
                sb.AppendLine($"                Description = \"{EscapeString(nodeTypeConfig?.Description ?? dataModel.Description)}\",");
                sb.AppendLine($"                IconName = \"{EscapeString(nodeTypeConfig?.IconName ?? dataModel.IconName)}\",");
                sb.AppendLine($"                DisplayOrder = {nodeTypeConfig?.DisplayOrder ?? dataModel.DisplayOrder}");
                sb.AppendLine(isLast ? "            }" : "            },");
            }
        }
        else
        {
            // Empty DataModels - generate placeholder configuration
            sb.AppendLine("            new NodeTypeConfiguration");
            sb.AppendLine("            {");
            sb.AppendLine($"                NodeType = \"{EscapeString(nodeTypeConfig?.NodeType ?? node.NodeType)}\",");
            sb.AppendLine("                DataType = typeof(object),");
            sb.AppendLine("                HubConfiguration = ConfigureHub,");
            sb.AppendLine($"                DisplayName = \"{EscapeString(nodeTypeConfig?.DisplayName ?? node.Name)}\",");
            sb.AppendLine($"                Description = \"{EscapeString(nodeTypeConfig?.Description ?? node.Description)}\",");
            sb.AppendLine($"                IconName = \"{EscapeString(nodeTypeConfig?.IconName ?? node.IconName)}\",");
            sb.AppendLine($"                DisplayOrder = {nodeTypeConfig?.DisplayOrder ?? node.DisplayOrder}");
            sb.AppendLine("            }");
        }
        sb.AppendLine("        ];");
        sb.AppendLine();

        // ConfigureHub method
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Hub configuration - registers all DataModel types and adds data collections.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        private static MessageHubConfiguration ConfigureHub(MessageHubConfiguration config)");
        sb.AppendLine("        {");

        if (dataModels.Count > 0)
        {
            sb.AppendLine("            var builder = config.ConfigureMeshHub();");
            sb.AppendLine();
            sb.AppendLine("            // Register all DataModel types");
            foreach (var dataModel in dataModels)
            {
                var typeName = ExtractTypeName(dataModel.TypeSource);
                sb.AppendLine($"            builder = builder.WithDataType(typeof(MeshWeaver.Graph.Dynamic.{typeName}));");
            }
            sb.AppendLine();
            sb.AppendLine("            var result = builder.Build();");
        }
        else
        {
            sb.AppendLine("            var result = config;");
        }

        sb.AppendLine();
        sb.AppendLine("            // Add default views (Details, Edit, Thumbnail, Metadata, Settings, Comments)");
        sb.AppendLine("            result = result.WithDefaultNodeViews();");
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

        // Generate .AddData() call if any DataModel has DataContextConfiguration
        var dataModelsWithConfig = dataModels
            .Where(dm => !string.IsNullOrWhiteSpace(dm.DataContextConfiguration))
            .ToList();

        if (dataModelsWithConfig.Count > 0)
        {
            sb.AppendLine("            // Apply DataContext configurations from DataModels");
            sb.AppendLine("            result = result.AddData(data =>");
            sb.AppendLine("            {");
            foreach (var dataModel in dataModelsWithConfig)
            {
                var safeId = SanitizeName(dataModel.Id);
                sb.AppendLine($"                data = ConfigureDataContext_{safeId}(data);");
            }
            sb.AppendLine("                return data;");
            sb.AppendLine("            });");
            sb.AppendLine();
        }

        sb.AppendLine(contentCollectionsCode);
        sb.AppendLine();
        sb.AppendLine("            return result;");
        sb.AppendLine("        }");

        // Generate DataContext configuration methods for each DataModel with DataContextConfiguration
        foreach (var dataModel in dataModelsWithConfig)
        {
            var safeId = SanitizeName(dataModel.Id);
            // Extract the lambda body (everything after "=>")
            var lambdaBody = ExtractLambdaBody(dataModel.DataContextConfiguration!);
            sb.AppendLine();
            sb.AppendLine($"        /// <summary>");
            sb.AppendLine($"        /// DataContext configuration from DataModel '{dataModel.Id}'.");
            sb.AppendLine($"        /// </summary>");
            sb.AppendLine($"        private static DataContext ConfigureDataContext_{safeId}(DataContext data)");
            sb.AppendLine("        {");
            sb.AppendLine($"            return {lambdaBody};");
            sb.AppendLine("        }");
        }

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
    /// Extracts the body of a lambda expression (everything after "=>").
    /// </summary>
    /// <param name="lambda">The full lambda expression (e.g., "data => data.AddSource(...)").</param>
    /// <returns>The lambda body (e.g., "data.AddSource(...)").</returns>
    private static string ExtractLambdaBody(string lambda)
    {
        var arrowIndex = lambda.IndexOf("=>", StringComparison.Ordinal);
        if (arrowIndex < 0)
            return lambda;

        return lambda.Substring(arrowIndex + 2).Trim();
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
