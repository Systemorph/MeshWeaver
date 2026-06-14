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

    public JsonFileParser(JsonSerializerOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<string> SupportedExtensions => [".json"];

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

    public string Serialize(MeshNode node)
    {
        return JsonSerializer.Serialize(node, _options);
    }

    public bool CanSerialize(MeshNode node) => true;
}
