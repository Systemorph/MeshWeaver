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

    public Task<MeshNode?> ParseAsync(string filePath, string content, string relativePath, CancellationToken ct = default)
    {
        try
        {
            var node = JsonSerializer.Deserialize<MeshNode>(content, _options);
            return Task.FromResult(node);
        }
        catch
        {
            return Task.FromResult<MeshNode?>(null);
        }
    }

    public Task<string> SerializeAsync(MeshNode node, CancellationToken ct = default)
    {
        return Task.FromResult(JsonSerializer.Serialize(node, _options));
    }

    public bool CanSerialize(MeshNode node) => true;
}
