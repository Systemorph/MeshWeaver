using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.Layout;

namespace MeshWeaver.Kernel;

/// <summary>
/// Parses markdown content into notebook cells and serializes cells back to markdown.
/// </summary>
public static partial class NotebookParser
{
    private static readonly Regex FencedCodeBlockRegex = FencedCodeBlockPattern();
    private static readonly Regex YamlFrontMatterRegex = YamlFrontMatterPattern();

    /// <summary>
    /// Parses markdown content into a list of notebook cell controls.
    /// Code blocks with --execute or --render flags become code cells.
    /// All other content becomes markdown cells.
    /// </summary>
    /// <param name="markdown">The markdown content to parse.</param>
    /// <returns>A list of notebook cell controls.</returns>
    public static ImmutableList<NotebookCellControl> ParseMarkdown(string markdown)
    {
        var cells = new List<NotebookCellControl>();

        // Remove YAML front matter if present
        var content = YamlFrontMatterRegex.Replace(markdown, "").TrimStart();

        var currentPosition = 0;
        var markdownBuffer = new StringBuilder();

        foreach (Match match in FencedCodeBlockRegex.Matches(content))
        {
            // Capture any markdown text before this code block
            if (match.Index > currentPosition)
            {
                var precedingText = content[currentPosition..match.Index].Trim();
                if (!string.IsNullOrWhiteSpace(precedingText))
                {
                    if (markdownBuffer.Length > 0)
                        markdownBuffer.AppendLine();
                    markdownBuffer.Append(precedingText);
                }
            }

            // Flush markdown buffer as a markdown cell if we hit a code block
            if (markdownBuffer.Length > 0)
            {
                cells.Add(NotebookCellControl.Markdown(markdownBuffer.ToString()));
                markdownBuffer.Clear();
            }

            // Parse the code block
            var language = match.Groups["language"].Value.Trim();
            var args = match.Groups["args"].Value.Trim();
            var code = match.Groups["code"].Value;

            // Remove trailing newline from code
            if (code.EndsWith("\n"))
                code = code[..^1];
            if (code.EndsWith("\r"))
                code = code[..^1];

            // Check if this is an executable code block
            var isExecutable = args.Contains("--execute") || args.Contains("--render");

            // Extract cell ID from args if present
            string? cellId = null;
            var idMatch = Regex.Match(args, @"--(?:execute|render)\s+(\S+)");
            if (idMatch.Success)
                cellId = idMatch.Groups[1].Value;

            // Map common language aliases to Monaco language identifiers
            var monacoLanguage = MapLanguage(language);

            if (isExecutable || !string.IsNullOrEmpty(monacoLanguage) && monacoLanguage != "plaintext")
            {
                var cell = NotebookCellControl.Code(code, monacoLanguage);
                if (cellId != null)
                    cell = cell.WithCellId(cellId);
                cells.Add(cell);
            }
            else
            {
                // Non-executable code block - treat as part of markdown
                markdownBuffer.AppendLine();
                markdownBuffer.Append(match.Value);
            }

            currentPosition = match.Index + match.Length;
        }

        // Capture any remaining markdown after the last code block
        if (currentPosition < content.Length)
        {
            var remainingText = content[currentPosition..].Trim();
            if (!string.IsNullOrWhiteSpace(remainingText))
            {
                if (markdownBuffer.Length > 0)
                    markdownBuffer.AppendLine();
                markdownBuffer.Append(remainingText);
            }
        }

        // Flush any remaining markdown buffer
        if (markdownBuffer.Length > 0)
        {
            cells.Add(NotebookCellControl.Markdown(markdownBuffer.ToString()));
        }

        // If no cells were created, add an empty code cell
        if (cells.Count == 0)
        {
            cells.Add(NotebookCellControl.Code("", "csharp"));
        }

        return cells.ToImmutableList();
    }

    /// <summary>
    /// Serializes notebook cell controls to markdown format.
    /// Code cells are rendered as fenced code blocks with --execute flag.
    /// Markdown cells are rendered as plain markdown.
    /// </summary>
    /// <param name="cells">The cells to serialize.</param>
    /// <param name="frontMatter">Optional YAML front matter to include.</param>
    /// <returns>The markdown content.</returns>
    public static string SerializeToMarkdown(IEnumerable<NotebookCellControl> cells, string? frontMatter = null)
    {
        var sb = new StringBuilder();

        // Add front matter if provided
        if (!string.IsNullOrWhiteSpace(frontMatter))
        {
            if (!frontMatter.StartsWith("---"))
                sb.AppendLine("---");
            sb.AppendLine(frontMatter.Trim());
            if (!frontMatter.TrimEnd().EndsWith("---"))
                sb.AppendLine("---");
            sb.AppendLine();
        }

        var isFirst = true;
        foreach (var cell in cells)
        {
            if (!isFirst)
                sb.AppendLine();

            var skin = cell.Skins.OfType<NotebookCellSkin>().FirstOrDefault();
            var cellType = skin?.CellType as NotebookCellType? ?? NotebookCellType.Code;
            var language = skin?.Language as string ?? "csharp";
            var cellId = skin?.CellId as string ?? "";
            var content = cell.Content as string ?? "";

            if (cellType == NotebookCellType.Code)
            {
                // Render as fenced code block with --execute flag
                var languageId = MapLanguageReverse(language);
                sb.AppendLine($"```{languageId} --execute {cellId}");
                sb.AppendLine(content);
                sb.AppendLine("```");
            }
            else
            {
                // Render as plain markdown
                sb.AppendLine(content);
            }

            isFirst = false;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Maps common language aliases to Monaco editor language identifiers.
    /// </summary>
    private static string MapLanguage(string language)
    {
        return language.ToLowerInvariant() switch
        {
            "csharp" or "c#" or "cs" => "csharp",
            "python" or "py" => "python",
            "javascript" or "js" => "javascript",
            "typescript" or "ts" => "typescript",
            "fsharp" or "f#" or "fs" => "fsharp",
            "markdown" or "md" => "markdown",
            "json" => "json",
            "xml" => "xml",
            "yaml" or "yml" => "yaml",
            "html" => "html",
            "css" => "css",
            "sql" => "sql",
            "bash" or "sh" or "shell" => "shell",
            "powershell" or "ps1" or "pwsh" => "powershell",
            "" => "plaintext",
            _ => language.ToLowerInvariant()
        };
    }

    /// <summary>
    /// Maps Monaco language identifiers back to common language names for code blocks.
    /// </summary>
    private static string MapLanguageReverse(string language)
    {
        return language.ToLowerInvariant() switch
        {
            "csharp" => "csharp",
            "python" => "python",
            "javascript" => "javascript",
            "typescript" => "typescript",
            "fsharp" => "fsharp",
            "markdown" => "markdown",
            _ => language
        };
    }

    [GeneratedRegex(@"^---\s*\n[\s\S]*?\n---\s*\n", RegexOptions.Multiline)]
    private static partial Regex YamlFrontMatterPattern();

    [GeneratedRegex(@"```(?<language>\w*)\s*(?<args>[^\n]*)\n(?<code>[\s\S]*?)```", RegexOptions.Multiline)]
    private static partial Regex FencedCodeBlockPattern();
}
