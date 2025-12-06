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

        string? token;
        var nextChar = line.PeekChar();

        if (nextChar == '(')
        {
            // Parentheses syntax: @("path") or @(path) - legacy support
            line.NextChar();
            token = ReadToken(ref line);
            if (string.IsNullOrWhiteSpace(token))
                return BlockState.None;

            Prune(ref line);
            if (line.PeekChar() != ')')
                return BlockState.None;
            line.NextChar();
        }
        else if (nextChar == '"')
        {
            // Quoted syntax without parentheses: @"path with spaces"
            token = ReadQuotedPath(ref line);
            if (string.IsNullOrWhiteSpace(token))
                return BlockState.None;
        }
        else if (char.IsLetterOrDigit(nextChar) || nextChar == '/')
        {
            // Direct syntax without parentheses: @app/Northwind/Overview
            // Defaults to area reference
            token = ReadDirectPath(ref line);
            if (string.IsNullOrWhiteSpace(token))
                return BlockState.None;
        }
        else
        {
            return BlockState.None;
        }

        // Try to parse as unified content reference (data:, content:, area:)
        var block = CreateBlockFromToken(token);
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
    private static readonly HashSet<char> DirectPathBreakChars = [' ', '\t', '\n', '\r', '\0', ')', ']', '}'];



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
    /// Reads a path directly after @ without parentheses.
    /// Stops at whitespace or end of line.
    /// Example: @app/Northwind/Overview
    /// </summary>
    private string? ReadDirectPath(ref StringSlice slice)
    {
        var token = new StringBuilder();
        while (true)
        {
            var c = slice.PeekChar();
            if (DirectPathBreakChars.Contains(c))
                break;

            token.Append(c);
            slice.NextChar();
        }

        return token.Length > 0 ? token.ToString() : null;
    }

    /// <summary>
    /// Reads a quoted path after @.
    /// Example: @"content:app/docs/My Report.pdf"
    /// </summary>
    private string? ReadQuotedPath(ref StringSlice slice)
    {
        // Skip opening quote
        if (slice.PeekChar() != '"')
            return null;
        slice.NextChar();

        var token = new StringBuilder();
        while (true)
        {
            var c = slice.PeekChar();
            if (c == '\0' || c == '\n' || c == '\r')
                return null; // Unclosed quote

            if (c == '"')
            {
                slice.NextChar(); // consume closing quote
                return token.ToString();
            }

            token.Append(c);
            slice.NextChar();
        }
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
