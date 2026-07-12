using System.Text.Json;
using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Persistence.Parsers;

/// <summary>
/// Parses .json files into MeshNode objects using the hub's JsonSerializerOptions.
/// Handles $type discriminators for polymorphic content (NodeTypeDefinition, etc.).
/// </summary>
public class JsonFileParser : IFileFormatParser
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Creates a JSON parser that serializes and deserializes MeshNode objects.
    /// </summary>
    /// <param name="options">Serializer options carrying the $type discriminator and converter configuration used for polymorphic content.</param>
    public JsonFileParser(JsonSerializerOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => [".json"];

    /// <inheritdoc />
    /// <remarks>
    /// A malformed document THROWS (<see cref="JsonException"/>) rather than returning null:
    /// <see cref="FileFormatParserRegistry.TryParse"/> catches per-parser failures and surfaces
    /// them through its <c>onError</c> callback, so an import pipeline can report the dropped
    /// file on its activity instead of silently losing the node (the old <c>catch → null</c>
    /// here swallowed the error before the registry ever saw it).
    /// </remarks>
    public MeshNode? Parse(string filePath, string content, string relativePath)
    {
        return JsonSerializer.Deserialize<MeshNode>(content, _options);
    }

    /// <inheritdoc />
    public string Serialize(MeshNode node)
    {
        return JsonSerializer.Serialize(node, _options);
    }

    /// <inheritdoc />
    public bool CanSerialize(MeshNode node) => true;
}
