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
    /// Reserved prefixes for unified references.
    /// These cannot be used as addressType values.
    /// </summary>
    private static readonly HashSet<string> ReservedPrefixes = ["data", "content", "area"];

    /// <summary>
    /// Creates the appropriate block type based on the token content.
    /// Supports unified reference notation (data/, content/, area/) and default format.
    /// </summary>
    private ContainerBlock CreateBlockFromToken(string token)
    {
        // Parse prefix and extract address/path
        var parsed = ParsePath(token);
        if (parsed == null)
        {
            // Fall back to legacy format (addressType/addressId/area)
            return new LayoutAreaComponentInfo(token, this);
        }

        var (prefix, addressType, addressId, remainingPath) = parsed.Value;
        var address = $"{addressType}/{addressId}";

        return prefix.ToLowerInvariant() switch
        {
            "data" => new LayoutAreaComponentInfo(
                address,
                DataAreaName,
                remainingPath,
                this),
            "content" => new LayoutAreaComponentInfo(
                address,
                ContentAreaName,
                remainingPath,
                this),
            "area" or "" => CreateAreaBlock(address, remainingPath),
            _ => new LayoutAreaComponentInfo(token, this)
        };
    }

    /// <summary>
    /// Creates an area block from the remaining path.
    /// Format: areaName[/areaId...] or areaName?queryParams
    /// The areaId includes all remaining path segments joined with '/'
    /// </summary>
    private ContainerBlock CreateAreaBlock(string address, string? remainingPath)
    {
        if (string.IsNullOrEmpty(remainingPath))
            return new LayoutAreaComponentInfo(address, "Default", null, this);

        // Check for ? separator (query params as area id)
        var queryIndex = remainingPath.IndexOf('?');
        if (queryIndex > 0)
        {
            var areaName = remainingPath[..queryIndex];
            var areaId = remainingPath[(queryIndex + 1)..];
            return new LayoutAreaComponentInfo(address, areaName, areaId, this);
        }

        // Check for / separator - areaId is everything after the first /
        var slashIndex = remainingPath.IndexOf('/');
        if (slashIndex > 0)
        {
            var areaName = remainingPath[..slashIndex];
            var areaId = remainingPath[(slashIndex + 1)..]; // Keep all remaining segments
            return new LayoutAreaComponentInfo(address, areaName, areaId, this);
        }

        return new LayoutAreaComponentInfo(address, remainingPath, null, this);
    }

    /// <summary>
    /// Parses a path into its components.
    /// Supports formats:
    /// - prefix/addressType/addressId/remainingPath (e.g., data/app/test/Collection)
    /// - addressType/addressId/remainingPath (defaults to area prefix)
    /// Reserved prefixes (data, content, area) are detected by first path segment.
    /// </summary>
    private (string Prefix, string AddressType, string AddressId, string? RemainingPath)? ParsePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        string prefix;
        int addressTypeIndex;

        // Check if first segment is a reserved prefix
        if (ReservedPrefixes.Contains(parts[0].ToLowerInvariant()))
        {
            prefix = parts[0].ToLowerInvariant();
            addressTypeIndex = 1;

            // Need at least prefix/addressType/addressId
            if (parts.Length < 3)
                return null;
        }
        else
        {
            prefix = "area"; // Default prefix
            addressTypeIndex = 0;
        }

        var addressType = parts[addressTypeIndex];
        var addressId = parts[addressTypeIndex + 1];
        var remainingPath = parts.Length > addressTypeIndex + 2
            ? string.Join("/", parts.Skip(addressTypeIndex + 2))
            : null;

        return (prefix, addressType, addressId, remainingPath);
    }
}
