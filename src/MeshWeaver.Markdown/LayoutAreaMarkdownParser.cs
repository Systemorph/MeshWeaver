using System.Text;
using Markdig.Helpers;
using Markdig.Parsers;

namespace MeshWeaver.Markdown;

public class LayoutAreaMarkdownParser : BlockParser
{
    private readonly object defaultAddress;

    public LayoutAreaMarkdownParser(object defaultAddress)
    {
        this.defaultAddress = defaultAddress;
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

        var layoutAreaComponentInfo = new LayoutAreaComponentInfo(area, this);
        processor.NewBlocks.Push(layoutAreaComponentInfo);

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


}
