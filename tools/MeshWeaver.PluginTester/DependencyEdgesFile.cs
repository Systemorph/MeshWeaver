using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The bake's forward dependency edges as ONE file beside the bundles —
/// <c>dependency-edges.json</c> (<c>Doc/Architecture/DependencyGraph</c> → <i>Where it runs</i>).
///
/// <para><b>Why a file.</b> The runtime compile stores each source's <see cref="DependencyEdgeSet"/>
/// as a satellite node in that environment's mesh. CI has no mesh: its selection (MeshWeaver.Plugins
/// <c>scripts/dependency-graph.py</c>) reads the graph of the trunk it branched from, and the only
/// place that graph is computed with no mesh is this bake. So the bake writes what it already
/// computed (<c>NodeSetCompiler.CompiledNode.DependencyEdges</c>) instead of dropping it.</para>
///
/// <para><b>What a reader can rely on.</b> Every NodeType the bake ATTEMPTED appears exactly once:
/// under <c>sets</c> with its edge set when it compiled (an EMPTY <c>edges</c> list is "extracted,
/// depends on nothing"), or under <c>failed</c> when it did not (no edges were read — never "depends
/// on nothing"). A type this bake did not attempt (a narrowed bake, a type with nothing to compile)
/// is absent, which a reader must treat as unknown. Every set carries <c>commit</c> — the tree it
/// was read from — so a reader can decide whether it still describes a later tree.</para>
/// </summary>
public static class DependencyEdgesFile
{
    /// <summary>The file name, beside <c>framework-mvid.txt</c> in the bake output.</summary>
    public const string FileName = "dependency-edges.json";

    /// <summary>The document's schema version; a reader refuses one it does not know.</summary>
    public const int Schema = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>The document written to <see cref="FileName"/>.</summary>
    /// <param name="Schema">See <see cref="DependencyEdgesFile.Schema"/>.</param>
    /// <param name="FrameworkIdentity">The host identity the bake compiled against.</param>
    /// <param name="Commit">The tree the bake read, or null when the producer did not know it.</param>
    /// <param name="Sets">One edge set per type that compiled, ordinal by source.</param>
    /// <param name="Failed">Types the bake attempted that produced no edge set, ordinal.</param>
    public sealed record Document(
        int Schema,
        string FrameworkIdentity,
        string? Commit,
        ImmutableArray<DependencyEdgeSet> Sets,
        ImmutableArray<string> Failed);

    /// <summary>Builds the document for a bake's attempted types.</summary>
    /// <param name="frameworkIdentity">The host identity.</param>
    /// <param name="commit">The tree the bake read.</param>
    /// <param name="types">Every type the bake attempted.</param>
    public static Document Of(string frameworkIdentity, string? commit, IEnumerable<TreeBake.TypeResult> types)
    {
        var all = types.ToList();
        var sets = all
            .Where(t => t.Success && t.DependencyEdges is not null)
            .Select(t => t.DependencyEdges! with { Commit = t.DependencyEdges!.Commit ?? commit })
            .OrderBy(s => s.Source, StringComparer.Ordinal)
            .ToImmutableArray();
        var failed = all
            .Where(t => !t.Success || t.DependencyEdges is null)
            .Select(t => t.NodePath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToImmutableArray();
        return new Document(Schema, frameworkIdentity, commit, sets, failed);
    }

    /// <summary>Writes <see cref="FileName"/> into <paramref name="outputDirectory"/>.</summary>
    /// <param name="outputDirectory">The bake output directory.</param>
    /// <param name="frameworkIdentity">The host identity.</param>
    /// <param name="commit">The tree the bake read.</param>
    /// <param name="types">Every type the bake attempted.</param>
    /// <returns>The path written.</returns>
    public static string Write(
        string outputDirectory, string frameworkIdentity, string? commit, IEnumerable<TreeBake.TypeResult> types)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, FileName);
        File.WriteAllText(path, Serialize(Of(frameworkIdentity, commit, types)));
        return path;
    }

    /// <summary>Writes an already-built document (the module workspace build's).</summary>
    /// <param name="outputDirectory">The output directory.</param>
    /// <param name="document">The document.</param>
    /// <returns>The path written.</returns>
    public static string WriteDocument(string outputDirectory, Document document)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, FileName);
        File.WriteAllText(path, Serialize(document));
        return path;
    }

    /// <summary>One set as the JSON a bundle entry carries (<c>BundleWriter.AssemblyEntry.DependencyEdges</c>),
    /// stamped with <paramref name="commit"/> when it names none.</summary>
    /// <param name="edges">The set.</param>
    /// <param name="commit">The tree the bake read.</param>
    public static JsonElement ToBundleJson(DependencyEdgeSet edges, string? commit) =>
        JsonSerializer.SerializeToElement(edges with { Commit = edges.Commit ?? commit }, Json);

    /// <summary>The document's JSON text (camelCase, nulls omitted).</summary>
    /// <param name="document">The document.</param>
    public static string Serialize(Document document) => JsonSerializer.Serialize(document, Json);

    /// <summary>Reads a document back; null for a schema this reader does not know.</summary>
    /// <param name="json">The file's text.</param>
    public static Document? Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<Document>(json, Json);
        return document is { Schema: Schema } ? document : null;
    }
}
