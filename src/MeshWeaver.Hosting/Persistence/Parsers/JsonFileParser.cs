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
    /// <para>A malformed document THROWS (<see cref="JsonException"/>) rather than returning null:
    /// <see cref="FileFormatParserRegistry.TryParse"/> catches per-parser failures and surfaces
    /// them through its <c>onError</c> callback, so an import pipeline can report the dropped
    /// file on its activity instead of silently losing the node (the old <c>catch → null</c>
    /// here swallowed the error before the registry ever saw it).</para>
    ///
    /// <para>🚨 But a well-formed document that is simply NOT A NODE is not an error, and must not
    /// be reported as one. A synced source repo is full of ordinary JSON — <c>package.json</c>,
    /// <c>tsconfig.json</c>, <c>launchSettings.json</c>, lock files — and deserializing those into a
    /// <see cref="MeshNode"/> has two bad outcomes and no good one: npm's <c>"version": "0.1.0"</c>
    /// (a string where <see cref="MeshNode.Version"/> is numeric) THREW, so every sync of a repo
    /// containing one reported a failed import forever; and a document that merely has no
    /// overlapping keys deserializes "successfully" into an all-default node, which is worse — a
    /// silent empty node in the mesh. Both were the same missing decision: whether this file is a
    /// node at all.</para>
    ///
    /// <para>It is answered structurally, by <see cref="LooksLikeMeshNode"/>, and a non-node returns
    /// <c>null</c> — the same silent "no node here" a <c>.yml</c> or <c>.py</c> already gets from
    /// having no registered parser at all. The extension having a parser is what made a
    /// non-node <c>.json</c> a hard error while identical content in a <c>.txt</c> was fine.</para>
    /// </remarks>
    public MeshNode? Parse(string filePath, string content, string relativePath)
    {
        // ONE parse: JsonDocument answers "is this a node?" and then feeds the materialization, so
        // a node file costs no more than before and a non-node file costs strictly less (it is no
        // longer materialized just to be thrown away). ParseFile runs this under .Merge(8) over
        // every file in a repo, so a second full pass would not be free.
        using var document = JsonDocument.Parse(content);
        return LooksLikeMeshNode(document.RootElement)
            ? document.RootElement.Deserialize<MeshNode>(_options)
            : null;
    }

    /// <summary>
    /// True when <paramref name="root"/> is shaped like an authored node file: a JSON object
    /// carrying at least one property that only a <see cref="MeshNode"/> has —
    /// <c>$type</c>, <c>id</c>, or <c>nodeType</c>.
    ///
    /// <para>🚨 The marker set is empirical, not a guess: all <b>597</b> authored node <c>.json</c>
    /// files across <c>src/MeshWeaver.Documentation/Data</c>, <c>samples/*/Data</c> and
    /// <c>content/</c> carry one, so nothing that parses today stops parsing. It is deliberately
    /// checked case-insensitively — the serializer writes camelCase, but a hand-authored file may
    /// use <c>Id</c>.</para>
    ///
    /// <para>Weaker markers are excluded on purpose. <c>name</c> and <c>description</c> are the two
    /// <see cref="MeshNode"/> fields ordinary package manifests also use, so treating either as a
    /// marker would re-admit exactly the files this exists to exclude.</para>
    /// </summary>
    private static bool LooksLikeMeshNode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in root.EnumerateObject())
            if (property.NameEquals("$type")
                || string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "nodeType", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <inheritdoc />
    public string Serialize(MeshNode node)
    {
        return JsonSerializer.Serialize(node, _options);
    }

    /// <inheritdoc />
    public bool CanSerialize(MeshNode node) => true;
}
