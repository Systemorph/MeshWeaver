using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Resolves @@path references in text by loading content from the mesh.
/// Used for expanding documentation references in agent system prompts and tool descriptions.
/// Supports recursive resolution (e.g., MeshPlugin.md embeds @@QuerySyntax which gets expanded too).
/// </summary>
public static class InlineReferenceResolver
{
    private static readonly Regex InlineReferencePattern =
        new(@"@@([^\s#\]]+)(?:#(\w+))?", RegexOptions.Compiled);

    private const int MaxRecursionDepth = 3;

    /// <summary>
    /// Resolves all @@path and @@path#section references in the given text.
    /// Loads referenced documents via MeshPlugin.Get and replaces the references with content.
    /// Recursively resolves nested @@ references up to a depth limit.
    /// </summary>
    public static Task<string> ResolveAsync(string text, IMessageHub hub, IAgentChat chat)
        => ResolveAsync(text, hub, chat, 0);

    private static async Task<string> ResolveAsync(string text, IMessageHub hub, IAgentChat chat, int depth)
    {
        if (depth >= MaxRecursionDepth)
            return text;

        var matches = InlineReferencePattern.Matches(text);
        if (matches.Count == 0)
            return text;

        var result = text;
        var meshPlugin = new MeshPlugin(hub, chat);
        var logger = hub.ServiceProvider.GetService(typeof(ILogger<MeshPlugin>)) as ILogger;

        foreach (Match match in matches)
        {
            var path = match.Groups[1].Value;
            var section = match.Groups[2].Success ? match.Groups[2].Value : null;

            try
            {
                // Load the document using MeshPlugin.Get
                var rawContent = await meshPlugin.Get($"@{path}");

                if (rawContent.StartsWith("Not found") || rawContent.StartsWith("Error"))
                {
                    logger?.LogWarning("Failed to resolve @@{Path}: {Result}", path, rawContent);
                    continue;
                }

                // Extract markdown content from the MeshNode JSON
                var content = ExtractMarkdownContent(rawContent, hub.JsonSerializerOptions);

                // If section specified, extract just that section
                if (!string.IsNullOrEmpty(section))
                {
                    content = ExtractSection(content, section);
                }

                // Recursively resolve nested @@ references in the loaded content
                content = await ResolveAsync(content, hub, chat, depth + 1);

                // Replace the reference with the resolved content
                result = result.Replace(match.Value, content);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Error resolving @@{Path}", path);
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts readable content from a MeshPlugin.Get response.
    /// MeshPlugin.Get returns JSON-serialized MeshNode. We extract the Content field
    /// and return it as readable text (markdown for documentation nodes).
    /// </summary>
    private static string ExtractMarkdownContent(string rawJson, JsonSerializerOptions options)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            // Try to extract the "content" property from the MeshNode JSON
            if (root.TryGetProperty("content", out var contentProp))
            {
                // Content is a plain string (markdown body)
                if (contentProp.ValueKind == JsonValueKind.String)
                    return contentProp.GetString() ?? rawJson;

                // Content is an object - look for a nested "content" property (e.g., MarkdownContent.Content)
                if (contentProp.ValueKind == JsonValueKind.Object &&
                    contentProp.TryGetProperty("content", out var innerContent) &&
                    innerContent.ValueKind == JsonValueKind.String)
                    return innerContent.GetString() ?? rawJson;
            }

            // If no content property, return the raw JSON as fallback
            return rawJson;
        }
        catch
        {
            // If JSON parsing fails, return the raw text
            return rawJson;
        }
    }

    /// <summary>
    /// Extracts a markdown section by heading name.
    /// Finds ## SectionName and extracts content until the next ## heading or end of document.
    /// </summary>
    internal static string ExtractSection(string markdown, string sectionName)
    {
        // Find ## SectionName and extract until next ## or end
        var pattern = $@"##\s+{Regex.Escape(sectionName)}[^\n]*\n([\s\S]*?)(?=\n##|\z)";
        var match = Regex.Match(markdown, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : markdown;
    }
}
