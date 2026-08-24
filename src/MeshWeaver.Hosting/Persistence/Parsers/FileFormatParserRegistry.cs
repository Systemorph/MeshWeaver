using System.Text.Json;
using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Persistence.Parsers;

/// <summary>
/// Registry for file format parsers. Selects the appropriate parser based on file extension.
/// For extensions with multiple parsers (e.g., .md), attempts parsing in priority order.
/// </summary>
public class FileFormatParserRegistry
{
    private readonly Dictionary<string, List<IFileFormatParser>> _parsersByExtension;
    private readonly List<IFileFormatParser> _parsers;

    /// <summary>
    /// Builds the registry with the built-in parsers (markdown, C#) in priority order, adding a
    /// JSON parser only when serializer options are supplied, and putting any
    /// <paramref name="contributedParsers"/> FIRST.
    ///
    /// <para>🚨 Priority order is load-bearing for <c>.md</c>: a contributed parser that recognises
    /// a specific front matter (the AI module's agent parser is the one in the box) must be tried
    /// BEFORE <see cref="MarkdownFileParser"/>, which accepts every <c>.md</c> file and would
    /// otherwise win by default — turning an Agent into a plain Markdown node with no error
    /// anywhere. That silent degradation is the whole reason the order is stated here rather than
    /// left to registration accident.</para>
    ///
    /// <para>The agent parser used to be hard-coded in this list, which is what made
    /// <c>MeshWeaver.Hosting</c> — core hosting — depend on <c>MeshWeaver.AI</c>. It now travels
    /// with the AI assembly and arrives through this parameter, so the platform can be built and
    /// run without AI at all.</para>
    /// </summary>
    /// <param name="jsonOptions">Serializer options used to construct the JSON parser; when null, no JSON parser is registered.</param>
    /// <param name="contributedParsers">Parsers contributed by modules, tried before the built-ins.</param>
    public FileFormatParserRegistry(
        JsonSerializerOptions? jsonOptions = null,
        IEnumerable<IFileFormatParser>? contributedParsers = null)
    {
        // Parsers are listed in priority order for each extension.
        _parsers =
        [
            ..(contributedParsers ?? []),   // e.g. the AI module's agent parser, ahead of Markdown
            new MarkdownFileParser(),       // Fallback for other .md files
            new CSharpFileParser(),
            ..( jsonOptions != null ? [new JsonFileParser(jsonOptions)] : Array.Empty<IFileFormatParser>())
        ];

        _parsersByExtension = new Dictionary<string, List<IFileFormatParser>>(StringComparer.OrdinalIgnoreCase);
        foreach (var parser in _parsers)
        {
            foreach (var ext in parser.SupportedExtensions)
            {
                if (!_parsersByExtension.TryGetValue(ext, out var list))
                {
                    list = [];
                    _parsersByExtension[ext] = list;
                }
                list.Add(parser);
            }
        }
    }

    /// <summary>
    /// Gets the first parser for the given file extension.
    /// For parsing with fallback support, use TryParse instead.
    /// </summary>
    /// <param name="extension">File extension including the dot (e.g., ".md").</param>
    /// <returns>First parser for the extension, or null if no parser handles it.</returns>
    public IFileFormatParser? GetParser(string extension)
    {
        return _parsersByExtension.TryGetValue(extension, out var list) && list.Count > 0
            ? list[0]
            : null;
    }

    /// <summary>
    /// Gets all parsers for the given file extension in priority order.
    /// </summary>
    /// <param name="extension">File extension including the dot (e.g., ".md").</param>
    /// <returns>List of parsers for the extension.</returns>
    public IReadOnlyList<IFileFormatParser> GetParsers(string extension)
    {
        return _parsersByExtension.TryGetValue(extension, out var list)
            ? list
            : [];
    }

    /// <summary>
    /// Attempts to parse content using parsers for the given extension.
    /// Tries each parser in priority order until one succeeds.
    /// Exceptions from individual parsers are caught, allowing fallback to the next parser;
    /// when EVERY parser for the extension failed and at least one threw, the last exception
    /// is surfaced through <paramref name="onError"/> — so a caller running as an activity can
    /// report the dropped file instead of silently losing it. A file whose extension simply has
    /// no registered parser (a non-node file — <c>.yml</c>, <c>.py</c>, …) never reports.
    /// </summary>
    /// <param name="extension">File extension including the dot.</param>
    /// <param name="filePath">Full path to the file.</param>
    /// <param name="content">File content.</param>
    /// <param name="relativePath">Path relative to the data root.</param>
    /// <param name="onError">Invoked with (<paramref name="relativePath"/>, last exception) when all parsers failed and at least one threw.</param>
    /// <returns>Parsed MeshNode or null if no parser can handle the content.</returns>
    public MeshNode? TryParse(
        string extension,
        string filePath,
        string content,
        string relativePath,
        Action<string, Exception>? onError = null)
    {
        var parsers = GetParsers(extension);
        content = WithoutBom(content);
        Exception? lastError = null;
        foreach (var parser in parsers)
        {
            try
            {
                var node = parser.Parse(filePath, content, relativePath);
                if (node != null)
                    return node;
            }
            catch (Exception ex)
            {
                // Parser failed, try next parser in chain; remember the failure so an
                // all-parsers-failed outcome is reportable rather than silent.
                lastError = ex;
            }
        }
        if (lastError is not null)
            onError?.Invoke(relativePath, lastError);
        return null;
    }

    /// <summary>
    /// Drops a leading UTF-8 BOM (U+FEFF) from decoded file text.
    ///
    /// <para>🚨 #1767: this is not cosmetic. A BOM'd file reached every parser as text beginning
    /// with U+FEFF and each format failed differently — JSON threw (the file was SKIPPED), and a
    /// C# node parsed but stopped recognising its <c>// &lt;meshweaver&gt;</c> heading, so it kept
    /// its code and silently lost its declared Id/DisplayName. `PensionFund` lost <b>62 of 72
    /// files</b> that way and gated ZERO NodeTypes while the run reported success.</para>
    ///
    /// <para>The BOM survives to this layer because package content arrives as BYTES (git tree,
    /// archive, API response) and is decoded with <c>Encoding.UTF8.GetString</c>, which preserves
    /// U+FEFF as a character — unlike <c>File.ReadAllText</c>, whose encoding detection strips it.
    /// So a path that only ever read from disk never saw this, which is why it went unnoticed for
    /// years: the defect lives on the package path, and only the package path.</para>
    ///
    /// <para>Stripping belongs HERE, once, rather than in each parser: a BOM is a property of the
    /// text encoding, not of any file format, and three parsers each remembering to handle it is
    /// three chances to forget.</para>
    /// </summary>
    public static string WithoutBom(string content) =>
        // '\uFEFF', spelled as an escape ON PURPOSE: the literal character is invisible in every
        // editor, so a copy-paste or a formatter could drop it and turn this into a no-op that
        // still compiles and still reads correctly (Copilot review, #1781). An invisible constant
        // is the last thing a BOM fix should contain.
        content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;

    /// <summary>
    /// Gets a parser that can serialize the given node.
    /// </summary>
    /// <param name="node">The node to serialize.</param>
    /// <returns>Parser that can serialize the node, or null if none found.</returns>
    public IFileFormatParser? GetSerializerFor(MeshNode node)
    {
        return _parsers.FirstOrDefault(p => p.CanSerialize(node));
    }

    /// <summary>
    /// Gets the C# file parser for direct code configuration parsing.
    /// </summary>
    public CSharpFileParser CSharpParser => (CSharpFileParser)_parsersByExtension[".cs"][0];

    /// <summary>
    /// All supported file extensions.
    /// </summary>
    public IEnumerable<string> SupportedExtensions => _parsersByExtension.Keys;
}
