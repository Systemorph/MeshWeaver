namespace MeshWeaver.Hosting.Persistence.Parsers;

/// <summary>
/// Registry for file format parsers. Selects the appropriate parser based on file extension.
/// </summary>
public class FileFormatParserRegistry
{
    private readonly Dictionary<string, IFileFormatParser> _parsersByExtension;
    private readonly List<IFileFormatParser> _parsers;

    public FileFormatParserRegistry()
    {
        _parsers =
        [
            new MarkdownFileParser(),
            new CSharpFileParser()
        ];

        _parsersByExtension = new Dictionary<string, IFileFormatParser>(StringComparer.OrdinalIgnoreCase);
        foreach (var parser in _parsers)
        {
            foreach (var ext in parser.SupportedExtensions)
            {
                _parsersByExtension[ext] = parser;
            }
        }
    }

    /// <summary>
    /// Gets a parser for the given file extension.
    /// </summary>
    /// <param name="extension">File extension including the dot (e.g., ".md").</param>
    /// <returns>Parser for the extension, or null if no parser handles it.</returns>
    public IFileFormatParser? GetParser(string extension)
    {
        return _parsersByExtension.GetValueOrDefault(extension);
    }

    /// <summary>
    /// Gets a parser that can serialize the given node.
    /// </summary>
    /// <param name="node">The node to serialize.</param>
    /// <returns>Parser that can serialize the node, or null if none found.</returns>
    public IFileFormatParser? GetSerializerFor(MeshWeaver.Mesh.MeshNode node)
    {
        return _parsers.FirstOrDefault(p => p.CanSerialize(node));
    }

    /// <summary>
    /// Gets the C# file parser for direct code configuration parsing.
    /// </summary>
    public CSharpFileParser CSharpParser => (CSharpFileParser)_parsersByExtension[".cs"];

    /// <summary>
    /// All supported file extensions.
    /// </summary>
    public IEnumerable<string> SupportedExtensions => _parsersByExtension.Keys;
}
