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
    public MeshNode? Parse(string filePath, string content, string relativePath)
    {
        try
        {
            return JsonSerializer.Deserialize<MeshNode>(content, _options);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public string Serialize(MeshNode node)
    {
        return JsonSerializer.Serialize(node, _options);
    }

    /// <inheritdoc />
    public bool CanSerialize(MeshNode node) => true;
}
