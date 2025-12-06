using System.Text;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;
using MeshWeaver.Data;

namespace MeshWeaver.Markdown;

public class LayoutAreaMarkdownParser : BlockParser
{

    public LayoutAreaMarkdownParser()
    {
        OpeningCharacters = ['@' ];
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

        // Try to parse as unified content reference (data:, content:, area:)
        var block = CreateBlockFromToken(area);
        processor.NewBlocks.Push(block);

        return BlockState.ContinueDiscard;

    }

    private void Prune(ref StringSlice line)
    {
        var c = line.PeekChar();
        while (IgnoreChars.Contains(c))
            c = line.NextChar();
    }

    private static readonly HashSet<char> IgnoreChars = [' ', '\t'];
    private static readonly HashSet<char> BreakChars = ['\n', '\r', '\0', '}'];
    private static readonly HashSet<char> EndTokenChars = ['=', ','];



    private string? ReadToken(ref StringSlice slice)
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

    /// <summary>
    /// Creates the appropriate block type based on the token content.
    /// Supports unified reference notation (data:, content:, area:) and legacy format.
    /// </summary>
    private ContainerBlock CreateBlockFromToken(string token)
    {
        // Try to parse as unified content reference
        if (ContentReference.TryParse(token, out var reference) && reference != null)
        {
            return reference switch
            {
                DataContentReference dataRef => new DataContentBlock(dataRef, this),
                FileContentReference fileRef => new FileContentBlock(fileRef, this),
                LayoutAreaContentReference areaRef => new LayoutAreaComponentInfo(
                    $"{areaRef.AddressType}/{areaRef.AddressId}",
                    areaRef.AreaName,
                    areaRef.AreaId,
                    this),
                _ => throw new InvalidOperationException($"Unknown reference type: {reference.GetType().Name}")
            };
        }

        // Fall back to legacy format (addressType/addressId/area)
        return new LayoutAreaComponentInfo(token, this);
    }
}
