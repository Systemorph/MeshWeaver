using System.Text.Json;
using MeshWeaver.ContentCollections;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// Emits the generated skeleton the portal compiles alongside a NodeType's own sources: the
/// assembly attribute, the <c>MeshNodeProviderAttribute</c> subclass with its <c>Nodes</c> property,
/// and the <c>ConfigureHub</c> method wrapping the NodeType's <c>configuration</c> lambda.
///
/// <para>🚨 <b>Without this the produced assembly is inert.</b> It would carry the user's types and
/// nothing else — no provider attribute, so nothing registers the NodeType, so the assembly cannot
/// stand in for a runtime compile no matter how cleanly it built. Verified the hard way: an earlier
/// build of this tool packed a ThreeBody assembly containing <c>NBodyAreas</c> but no
/// <c>MeshNodeProvider</c>, <c>ConfigureHub</c> or <c>MeshWeaver.Graph.Generated</c> at all.</para>
///
/// <para>🚨 <b>And the lambda is CODE.</b> <c>content.configuration</c> is C# stored in a JSON
/// string, so no compiler has ever seen it: not <c>dotnet build</c> (node trees are
/// <c>&lt;None&gt;</c>), not a repo grep with a <c>*.cs</c> filter. On 2026-08-09 the framework
/// deleted <c>AddTracking()</c> while <c>SocialMedia/Post</c>, <c>Profile</c> and <c>PostsHub</c>
/// each still called it from that field; CI was green and all three production portals hit
/// <c>REFUSING READINESS</c> on the next framework bump. Compiling the skeleton type-checks it.</para>
///
/// <para>The generation is delegated to <see cref="DynamicMeshNodeAttributeGenerator"/> — the
/// framework's own, reached through <c>InternalsVisibleTo</c> — never reproduced here. A second
/// implementation is free to drift from the one that actually runs, and a skeleton that differs
/// from the runtime's is worse than none: it compiles, packs, installs, and then behaves
/// differently from what every test exercised.</para>
/// </summary>
public static class SkeletonGenerator
{
    /// <summary>File name of the emitted skeleton inside a unit's generated project.</summary>
    public const string FileName = "__MeshWeaverSkeleton.g.cs";

    /// <summary>
    /// Writes the skeleton for <paramref name="unit"/> into <paramref name="projectDirectory"/>,
    /// or returns null when the owning node cannot be read.
    /// </summary>
    public static string? Emit(PluginUnit unit, string projectDirectory)
    {
        var owner = Path.GetDirectoryName(unit.SourceDirectory)!;
        var nodeFile = new[] { Path.Combine(owner, "index.json"), owner + ".json" }
            .FirstOrDefault(File.Exists);
        if (nodeFile is null)
            return null;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(nodeFile));
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        var content = root.TryGetProperty("content", out var c) ? c : default;

        var node = new MeshNode(
            Id: Text(root, "id") ?? Path.GetFileName(owner),
            Namespace: Text(root, "namespace"))
        {
            Name = Text(root, "name") ?? string.Empty,
            // The generator emits this verbatim into the Nodes property. A NodeType node's own
            // nodeType is "NodeType"; a Space/plugin root carries its own — either way it is
            // metadata, copied through.
            NodeType = Text(root, "nodeType") ?? MeshNode.NodeTypePath,
            Icon = Text(root, "icon") ?? string.Empty,
            // 🚨 Carried through, not defaulted. The generator emits both verbatim into the Nodes
            // property (`Order = …`, `LastModified = DateTimeOffset.Parse("…")`), so leaving them
            // unset does not merely lose metadata — it makes the CI-built assembly DECLARE
            // something different from what the runtime would declare for the same node
            // (`0001-01-01` for a node the portal knows the real date of). The whole point of
            // delegating to the framework's generator is that the two outputs agree; feeding it
            // different inputs defeats that just as surely as reimplementing it.
            Order = Number(root, "order"),
            LastModified = Timestamp(root, "lastModified") ?? default,
        };

        var source = new DynamicMeshNodeAttributeGenerator().GenerateAttributeSource(
            node,
            // null suppresses user-code emission: the sources are separate Compile items, exactly
            // as the runtime keeps them separate syntax trees (AssembleCompilationInputs).
            codeFile: null,
            hubConfiguration: Text(content, "configuration") ?? Text(content, "hubConfiguration"),
            contentCollections: ReadContentCollections(content));

        var path = Path.Combine(projectDirectory, FileName);
        File.WriteAllText(path, source);
        return path;
    }

    private static int? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>
    /// A node timestamp. An unparsable value yields null (and therefore the default) rather than
    /// throwing: a malformed date must not stop a plugin building, and the value is metadata.
    /// </summary>
    private static DateTimeOffset? Timestamp(JsonElement element, string property) =>
        Text(element, property) is { } text
        && DateTimeOffset.TryParse(
            text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The node's declared content collections. They contribute an <c>AddContentCollections(…)</c>
    /// block to <c>ConfigureHub</c>, so an assembly built without them would register a hub missing
    /// its collections — the view renders, the files are absent.
    /// </summary>
    private static List<ContentCollectionConfig>? ReadContentCollections(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("contentCollections", out var collections)
            || collections.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<ContentCollectionConfig>();
        foreach (var element in collections.EnumerateArray())
        {
            var name = Text(element, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            result.Add(new ContentCollectionConfig
            {
                Name = name,
                SourceType = Text(element, "sourceType") ?? string.Empty,
                DisplayName = Text(element, "displayName"),
            });
        }
        return result.Count > 0 ? result : null;
    }
}
