using System.Text;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace MeshWeaver.Markdown;

public class LayoutAreaMarkdownParser : BlockParser
{

    public LayoutAreaMarkdownParser()
    {
        OpeningCharacters = ['@'];
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

        // Check for double @@ (inline rendering) vs single @ (hyperlink)
        bool isInline = false;
        var nextChar = line.PeekChar();
        if (nextChar == '@')
        {
            isInline = true;
            line.NextChar(); // consume second @
            nextChar = line.PeekChar();
        }

        string? token;

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
            // Direct syntax without parentheses: @addressType/addressId/areaName
            // Format: addressType/addressId[/keyword[/remainingPath]]
            // Defaults to area reference if no keyword
            token = ReadDirectPath(ref line);
            if (string.IsNullOrWhiteSpace(token))
                return BlockState.None;
        }
        else
        {
            return BlockState.None;
        }

        // Try to parse as unified content reference (addressType/addressId/keyword/...)
        var block = CreateBlockFromToken(token, isInline);
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
    /// Example: @"content/app/docs/My Report.pdf"
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
    /// Area name for data references. Uses $ prefix to avoid name collisions.
    /// </summary>
    public const string DataAreaName = "$Data";

    /// <summary>
    /// Area name for content/file references. Uses $ prefix to avoid name collisions.
    /// </summary>
    public const string ContentAreaName = "$Content";

    /// <summary>
    /// Reserved keywords for unified references.
    /// When one of these appears as the third segment, it determines the reference type.
    /// </summary>
    private static readonly HashSet<string> ReservedKeywords = ["data", "content", "area"];

    /// <summary>
    /// Creates the appropriate block type based on the token content.
    /// Supports unified reference notation (addressType/addressId/keyword/...) and default format.
    /// Format: addressType/addressId[/keyword[/remainingPath]]
    /// If no keyword is specified, defaults to area.
    /// </summary>
    /// <param name="token">The parsed token/path</param>
    /// <param name="isInline">True for @@ (inline rendering), false for @ (hyperlink)</param>
    private ContainerBlock CreateBlockFromToken(string token, bool isInline)
    {
        // Parse address and extract keyword/path
        var parsed = ParsePath(token);
        if (parsed == null)
        {
            // Fall back to legacy format
            return new LayoutAreaComponentInfo(token, this, isInline);
        }

        var (addressType, addressId, keyword, remainingPath) = parsed.Value;
        var address = $"{addressType}/{addressId}";

        return keyword.ToLowerInvariant() switch
        {
            "data" => new LayoutAreaComponentInfo(
                address,
                DataAreaName,
                remainingPath,
                this,
                isInline),
            "content" => new LayoutAreaComponentInfo(
                address,
                ContentAreaName,
                remainingPath,
                this,
                isInline),
            "area" or "" => CreateAreaBlock(address, remainingPath, isInline),
            _ => new LayoutAreaComponentInfo(token, this, isInline)
        };
    }

    /// <summary>
    /// Creates an area block from the remaining path.
    /// Format: areaName[/areaId...] or areaName?queryParams
    /// The areaId includes all remaining path segments joined with '/'
    /// When no area is specified, null is passed to let the layout system determine the default.
    /// </summary>
    /// <param name="address">The resolved address</param>
    /// <param name="remainingPath">The remaining path after address</param>
    /// <param name="isInline">True for @@ (inline rendering), false for @ (hyperlink)</param>
    private ContainerBlock CreateAreaBlock(string address, string? remainingPath, bool isInline)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return new LayoutAreaComponentInfo(address, null, null, this, isInline);

        // Check for ? separator (query params as area id)
        var queryIndex = remainingPath.IndexOf('?');
        if (queryIndex > 0)
        {
            var areaName = remainingPath[..queryIndex];
            var areaId = remainingPath[(queryIndex + 1)..];
            return new LayoutAreaComponentInfo(address, areaName, areaId, this, isInline);
        }

        // Check for / separator - areaId is everything after the first /
        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex > 0)
        {
            var areaName = remainingPath[..slashIndex];
            var areaId = remainingPath[(slashIndex + 1)..]; // Keep all remaining segments
            return new LayoutAreaComponentInfo(address, areaName, areaId, this, isInline);
        }

        return new LayoutAreaComponentInfo(address, remainingPath, null, this, isInline);
    }

    /// <summary>
    /// Parses a path into its components.
    /// Format: addressType/addressId[/keyword[/remainingPath]]
    /// If keyword is not a reserved keyword (data, content, area), it's treated as part of remainingPath
    /// and the keyword defaults to "area".
    /// </summary>
    private (string AddressType, string AddressId, string Keyword, string? RemainingPath)? ParsePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        var addressType = parts[0];
        var addressId = parts[1];

        string keyword;
        string? remainingPath;

        if (parts.Length >= 3 && ReservedKeywords.Contains(parts[2].ToLowerInvariant()))
        {
            // Explicit keyword specified (e.g., host/1/data/Collection)
            keyword = parts[2].ToLowerInvariant();
            remainingPath = parts.Length > 3
                ? string.Join("/", parts.Skip(3))
                : null;
        }
        else
        {
            // No keyword or unrecognized keyword - default to area
            keyword = "area";
            remainingPath = parts.Length > 2
                ? string.Join("/", parts.Skip(2))
                : null;
        }

        return (addressType, addressId, keyword, remainingPath);
    }
}
