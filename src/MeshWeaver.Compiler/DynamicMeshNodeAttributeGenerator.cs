using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;

namespace MeshWeaver.Compiler;

/// <summary>
/// Generates C# source code for dynamic MeshNodeProviderAttribute classes.
/// The generated attribute includes any user code and HubConfiguration.
/// </summary>
internal class DynamicMeshNodeAttributeGenerator
{
    /// <summary>
    /// The namespaces the generated skeleton puts in scope for user code. Immutable, written once,
    /// never mutated at runtime — a constant lookup, not a cache.
    ///
    /// <para>🚨 This list is the ONE declaration of "what a NodeType source can assume is imported",
    /// and BOTH compile paths must read it from here. The emit path gets it by concatenating user
    /// code INTO the skeleton file (<c>GenerateAttributeSource</c>, so these are ordinary
    /// file-scoped usings that cover the concatenated whole); the language-service path keeps one
    /// syntax tree PER source file for Monaco positions, so it gets it from
    /// <see cref="GenerateGlobalUsingsSource"/> instead. Two renderings of one list — never two
    /// lists (Systemorph/MeshWeaver#1802).</para>
    /// </summary>
    internal static readonly ImmutableArray<string> StandardUsings =
    [
        "System",
        "System.Collections.Generic",
        "System.ComponentModel",
        "System.ComponentModel.DataAnnotations",
        "System.Linq",
        "System.Reactive.Linq",
        "System.Text.Json.Serialization",
        "MeshWeaver.Mesh",
        "MeshWeaver.Messaging",
        "MeshWeaver.Data",
        "MeshWeaver.Domain",
        "MeshWeaver.Graph",
        "MeshWeaver.Graph.Configuration",
        "MeshWeaver.Layout",
        "MeshWeaver.Layout.Composition",
        "MeshWeaver.Layout.Domain",
        "MeshWeaver.Layout.Views",
        "MeshWeaver.Application.Styles",
        "MeshWeaver.ContentCollections",
        "MeshWeaver.Mesh.Services",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Configuration",
    ];

    /// <summary>
    /// Renders the compile's import scope as <c>global using</c> directives, for the
    /// language-service path — which keeps ONE SYNTAX TREE PER SOURCE FILE so a diagnostic, hover
    /// or completion carries the MeshNode path the user is actually editing.
    ///
    /// <para><b>Why this exists (#1802).</b> The emit path concatenates every source into the
    /// skeleton file, so <see cref="StandardUsings"/> and every file's own <c>using</c>s cover the
    /// whole compilation. A C# <c>using</c> is <b>file-scoped</b>, so under per-file trees the
    /// skeleton's imports reach NOTHING — and the language service reported phantom CS0246/CS0308
    /// on source that compiles and ships. <c>global using</c> is the language feature for exactly
    /// this: declared in one tree, in scope for the whole compilation. That restores emit's scope
    /// WITHOUT collapsing the per-file trees the language service exists to provide.</para>
    ///
    /// <para>The union of the authored <c>using</c>s is included for the same reason: concatenation
    /// hoists them to the top of the single emitted file, so under emit every file already sees
    /// every other file's imports. Reproducing that here uses the SAME
    /// <see cref="ExtractUsingStatements"/> the emit path hoists with, so the two cannot drift.</para>
    /// </summary>
    /// <param name="userSources">The effective source bodies of the compilation — for a speculative
    /// check, with the proposed body already substituted, so a <c>using</c> the user just typed
    /// counts.</param>
    public string GenerateGlobalUsingsSource(IEnumerable<string?> userSources)
    {
        // Ordinal set: `using` directives are case-sensitive, and a stable order keeps the
        // generated document byte-identical across calls (it is a cache key upstream).
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var ns in StandardUsings)
            namespaces.Add(ns);

        foreach (var code in userSources)
        {
            var (usings, _) = ExtractUsingStatements(code);
            foreach (var directive in usings)
            {
                // ExtractUsingStatements returns whole lines ("using X;", possibly indented, and
                // possibly `using static X;` or an alias). Only a plain namespace import can be
                // promoted to `global using` verbatim; an alias/static form is emitted as-is with
                // the global modifier, which C# also accepts.
                var trimmed = directive.Trim();
                if (trimmed.StartsWith("using ", StringComparison.Ordinal))
                    namespaces.Add(trimmed["using ".Length..].TrimEnd(';', ' '));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated import scope for language services — do not edit.");
        sb.AppendLine("// Mirrors the emit path's concatenated-file scope; see #1802.");
        foreach (var ns in namespaces)
            sb.AppendLine($"global using {ns};");
        return sb.ToString();
    }

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

        // Using statements (standard ones) — read from the single declaration so the emit path and
        // the language service's `global using` rendering cannot drift apart (#1802).
        foreach (var ns in StandardUsings)
            sb.AppendLine($"using {ns};");

        // User-defined using statements (extracted from code files)
        foreach (var userUsing in userUsings)
        {
            sb.AppendLine(userUsing);
        }
        sb.AppendLine();

        // Assembly attribute - MUST come before any namespace declarations
        sb.AppendLine($"[assembly: MeshWeaver.Graph.Generated.{safeClassName}MeshNodeProvider]");
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

        // MeshNodeProviderAttribute class
        sb.AppendLine($"    public class {safeClassName}MeshNodeProviderAttribute : MeshNodeProviderAttribute");
        sb.AppendLine("    {");

        // Nodes property
        sb.AppendLine("        public override IEnumerable<MeshNode> Nodes =>");
        sb.AppendLine("        [");
        sb.AppendLine($"            new MeshNode(\"{EscapeString(node.Path)}\")");
        sb.AppendLine("            {");
        sb.AppendLine($"                Name = \"{EscapeString(node.Name)}\",");
        sb.AppendLine($"                NodeType = \"{EscapeString(node.NodeType)}\",");
        sb.AppendLine($"                Icon = \"{EscapeString(node.Icon)}\",");
        sb.AppendLine($"                Order = {(node.Order.HasValue ? node.Order.Value.ToString() : "null")},");
        sb.AppendLine($"                LastModified = DateTimeOffset.Parse(\"{node.LastModified:O}\"),");
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

            // Check if this is a using DIRECTIVE (not a using DECLARATION inside a method).
            // `using var x = new T { ... };` has no parentheses and ends with ';', so the
            // paren check alone does not exclude it — hoisting it to the top of the
            // generated file yanks the variable out of its method and breaks compilation
            // (CS8805/CS0103). A directive never starts with `using var `.
            if (trimmed.StartsWith("using ")
                && !trimmed.StartsWith("using var ")
                && trimmed.EndsWith(";")
                && !trimmed.Contains("("))
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
