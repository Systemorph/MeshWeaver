using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Primitives;
using OpenSmc.Messaging;

namespace OpenSmc.Documentation.Markdown;

public class MarkdownComponentParser(IMessageHub hub)
{
    private readonly Dictionary<
        string,
        Func<LayoutAreaComponentInfo, string, LayoutAreaComponentInfo>
    > FieldMappings =
        new()
        {
            { "Area", (a, foundValue) => a with { Area = foundValue } },
            { "Id", (a, foundValue) => a with { Id = foundValue } },
            {
                "Options",
                (area, foundValue) =>
                    area with
                    {
                        Options = JsonSerializer.Deserialize<ImmutableDictionary<string, StringValues>>(
                            foundValue,
                            hub.JsonSerializerOptions
                        )
                    }
            },
            { "SourceReference", (a, foundValue) => a with { SourceReference = foundValue } },
            { "Address", (a, foundValue) => a with { Address = foundValue } },
            {
                "Source",
                (a, foundValue) =>
                    a with
                    {
                        Source = JsonSerializer.Deserialize<SourceInfo>(
                            foundValue,
                            hub.JsonSerializerOptions
                        )
                    }
            },
            {
                "DisplayMode",
                (a, foundValue) =>
                    a with
                    {
                        DisplayMode = JsonSerializer.Deserialize<DisplayMode>(
                            foundValue,
                            hub.JsonSerializerOptions
                        )
                    }
            }
        };

    public List<object> ParseMarkdown(string markdown)
    {
        var components = new List<object>();
        var lines = markdown.Split('\n');

        // Regex to capture Area and optional JSON-like structure
        var componentRegex = new Regex(@"\[LayoutArea Area=""(.*?)""(?:, (.*))?\]");

        foreach (var line in lines)
        {
            var match = componentRegex.Match(line);
            if (match.Success)
            {
                var area = match.Groups[1].Value;
                var optionsJson = "{" + match.Groups[2].Value + "}";
                var layoutAreaInfo = new LayoutAreaComponentInfo(area);

                if (!string.IsNullOrEmpty(match.Groups[2].Value))
                {
                    // Parse the JSON-like structure into a Dictionary to extract optional fields
                    var optionalProperties = JsonSerializer.Deserialize<
                        Dictionary<string, JsonElement>
                    >(optionsJson);

                    foreach (var property in optionalProperties)
                    {
                        if (FieldMappings.TryGetValue(property.Key, out var updateFunc))
                        {
                            var foundValue = property.Value.GetRawText();
                            // Adjust foundValue if it's a simple string, not a JSON object or array
                            if (property.Value.ValueKind == JsonValueKind.String)
                            {
                                foundValue = property.Value.GetString();
                            }
                            layoutAreaInfo = updateFunc(layoutAreaInfo, foundValue);
                        }
                    }
                }

                components.Add(layoutAreaInfo);
            }
        }

        return components;
    }

    private string ConvertMarkdownToHtml(string markdownLine)
    {
        // Placeholder for Markdown to HTML conversion
        // In a real application, use a library like Markdig
        return markdownLine;
    }
}
