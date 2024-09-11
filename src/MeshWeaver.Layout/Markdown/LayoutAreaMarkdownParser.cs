using System.Text;
using System.Text.Json;
using Markdig.Helpers;
using Markdig.Parsers;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Markdown;

public class LayoutAreaMarkdownParser : BlockParser
{
    private readonly Dictionary<string, Action<LayoutAreaComponentInfo, string>> fieldMappings;
    public readonly List<LayoutAreaComponentInfo> Areas = new();

    public LayoutAreaMarkdownParser(IMessageHub hub)
    {
        OpeningCharacters = ['@'];

        fieldMappings = new()
        {
            { nameof(LayoutAreaComponentInfo.Id), (a, foundValue) => a.Id = foundValue },
            { nameof(LayoutAreaComponentInfo.QueryString), (a, foundValue) => a.QueryString = foundValue },
            { nameof(LayoutAreaComponentInfo.Layout), (a, foundValue) => a.Layout = foundValue },
            { nameof(LayoutAreaComponentInfo.DivId), (a, foundValue) => a.DivId = foundValue },
            {
                nameof(LayoutAreaComponentInfo.Address), (a, foundValue) =>
                    a.Address = JsonSerializer.Deserialize<object>(foundValue, hub.JsonSerializerOptions)
            },
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
        Prune(ref line);
        if (line.PeekChar() != '(' )
            return BlockState.None;
        line.NextChar();

        // no area specified ==> cannot render.
        var area = ReadToken(ref line);
        if(string.IsNullOrWhiteSpace(area))
            return BlockState.None;

        Prune(ref line);
        // this would be syntax error ==> we just return nothing.
        if (line.PeekChar() != ')')
            return BlockState.None;

        line.NextChar();

        if (string.IsNullOrWhiteSpace(area))
            return BlockState.None;

        var layoutAreaComponentInfo = new LayoutAreaComponentInfo(area, this);
        Areas.Add(layoutAreaComponentInfo);

        Prune(ref line);

        if (line.PeekChar() != '{')
        {
            processor.NewBlocks.Push(layoutAreaComponentInfo);
            return BlockState.ContinueDiscard;
        }

        line.NextChar();

        while (ParseParameters(ref line, layoutAreaComponentInfo))
        { }

        processor.NewBlocks.Push(layoutAreaComponentInfo);
        return BlockState.ContinueDiscard;

    }

    private void Prune(ref StringSlice line)
    {
        var c = line.PeekChar();
        while (IgnoreChars.Contains(c))
            c = line.NextChar();
    }

    private bool ParseParameters(ref StringSlice slice, LayoutAreaComponentInfo info)
    {
        var name = ReadToken(ref slice);
        if (name == null || name.IsEmpty())
            return false;
        var value = ReadToken(ref slice);
        if(value == null || value.IsEmpty())
            return false;
        ParseToInfo(info, name, value);
        return true;
    }

    private static readonly HashSet<char> IgnoreChars = [' ', '\t'];
    private static readonly HashSet<char> BreakChars = ['\n', '\r', '\0', '}'];
    private static readonly HashSet<char> EndTokenChars = ['=', ','];

    public LayoutAreaMarkdownParser(Dictionary<string, Action<LayoutAreaComponentInfo, string>> fieldMappings)
    {
        this.fieldMappings = fieldMappings;
    }


    private string ReadToken(ref StringSlice slice)
    {
        var isInsideQuote = false;
        var token = new StringBuilder();
        while (true)
        {
            var c = slice.PeekChar();
            while (IgnoreChars.Contains(c) && !isInsideQuote)
            {
                slice.NextChar();
                c = slice.PeekChar();
            }

            if (BreakChars.Contains(c)) // End of line
                break;
            if (c == '"')
            {
                isInsideQuote = !isInsideQuote;
                slice.NextChar();
                if (isInsideQuote)
                    continue;
                return token.ToString();
            }

            if (!isInsideQuote && EndTokenChars.Contains(c))
            {
                slice.NextChar();
                if (token.Length == 0)
                    continue;
                return token.ToString();
            }

            token.Append(c);
            slice.NextChar();

        }

        return null;

    }

    private void ParseToInfo(LayoutAreaComponentInfo info, string paramName, string paramValue)
    {

        if (paramName.Length > 0 && paramValue.Length > 0)
        {
            var name = paramName.Trim();
            var value = paramValue.Trim();

            if (fieldMappings.TryGetValue(name, out var action))
            {
                action(info, value);
            }


        }
    }

}
