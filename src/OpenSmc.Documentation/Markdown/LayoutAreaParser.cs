using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Markdig.Parsers;
using Markdig.Syntax;
using Microsoft.Extensions.Primitives;
using OpenSmc.Messaging;

namespace OpenSmc.Documentation.Markdown;

public class LayoutAreaParser : BlockParser
{
    private readonly Dictionary<string, Action<LayoutAreaComponentInfo, string>> FieldMappings;
    public readonly List<LayoutAreaComponentInfo> Areas = new();

    public LayoutAreaParser(IMessageHub hub)
    {
        OpeningCharacters = ['@'];

        FieldMappings = new()
        {
            { "Id", (a, foundValue) => a.Id = foundValue },
            {
                "Options",
                (area, foundValue) =>
                    area.Options = JsonSerializer.Deserialize<
                        ImmutableDictionary<string, object>
                    >(foundValue, hub.JsonSerializerOptions)
            },
            { "SourceReference", (a, foundValue) => a.SourceReference = foundValue },
            { "Address", (a, foundValue) => a.Address = foundValue },
            {
                "Source",
                (a, foundValue) =>
                    a.Source = JsonSerializer.Deserialize<SourceInfo>(
                        foundValue,
                        hub.JsonSerializerOptions
                    )
            },
            {
                "DisplayMode",
                (a, foundValue) =>
                    a.DisplayMode = JsonSerializer.Deserialize<DisplayMode>(
                        foundValue,
                        hub.JsonSerializerOptions
                    )
            }
        };
    }

    public override BlockState TryOpen(BlockProcessor processor)
    {
        // We expect no indentation for a figure block.
        if (processor.IsCodeIndent)
        {
            return BlockState.None;
        }

        // Match fenced char
        var line = processor.Line;
        if (line.NextChar() != '(' || line.NextChar() != '"')
            return BlockState.None;

        string area = string.Empty;
        var c = line.NextChar();
        while (c != '"')
        {
            area += c;
            c = line.NextChar();
        }
        // this would be syntax error ==> we just return nothing.
        if (line.NextChar() != ')')
            return BlockState.None;

        // no area specified ==> cannot render.
        if (string.IsNullOrWhiteSpace(area))
            return BlockState.None;

        var layoutAreaComponentInfo = new LayoutAreaComponentInfo(area, this);
        Areas.Add(layoutAreaComponentInfo);

        c = line.PeekChar();
        if (c != '{')
        {
            processor.NewBlocks.Push(layoutAreaComponentInfo);
            return BlockState.ContinueDiscard;
        }

        return ParseParameters(processor, layoutAreaComponentInfo);
    }

    private BlockState ParseParameters(
        BlockProcessor processor,
        LayoutAreaComponentInfo layoutAreaComponentInfo
    )
    {
        var line = processor.Line;
        var parameters = new Dictionary<string, string>();
        var paramName = new StringBuilder();
        var paramValue = new StringBuilder();
        bool isReadingName = true;
        bool isInsideQuote = false;

        // Skip the initial '{'
        line.NextChar();

        while (true)
        {
            var c = line.NextChar();
            if (c == '\0') // End of line
                break;

            if (isReadingName)
            {
                if (c == '=' && !isInsideQuote)
                {
                    isReadingName = false;
                }
                else if (c != ' ' || isInsideQuote)
                {
                    paramName.Append(c);
                }
            }
            else // Reading value
            {
                if (c == '"' && !isInsideQuote)
                {
                    isInsideQuote = true;
                }
                else if (c == '"' && isInsideQuote)
                {
                    isInsideQuote = false;
                }
                else if (c == ',' && !isInsideQuote)
                {
                    parameters[paramName.ToString().Trim()] = paramValue.ToString().Trim();
                    paramName.Clear();
                    paramValue.Clear();
                    isReadingName = true;
                }
                else
                {
                    paramValue.Append(c);
                }
            }
        }

        if (paramName.Length > 0 && paramValue.Length > 0)
        {
            parameters[paramName.ToString().Trim()] = paramValue.ToString().Trim();
        }

        // Apply parameters using FieldMappings
        foreach (var param in parameters)
        {
            if (FieldMappings.TryGetValue(param.Key, out var action))
            {
                action(layoutAreaComponentInfo, param.Value);
            }
        }

        processor.NewBlocks.Push(layoutAreaComponentInfo);
        return BlockState.ContinueDiscard;
    }

    public override BlockState TryContinue(BlockProcessor processor, Block block)
    {
        //TODO Roland Bürgi 2024-07-14: We need to see if layout area goes across multiple lines.
        return BlockState.Continue;
    }
}
