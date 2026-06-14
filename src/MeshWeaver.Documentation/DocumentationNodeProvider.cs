using System.Reflection;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Documentation;

/// <summary>
/// Provides MeshWeaver platform documentation as static MeshNodes
/// loaded from embedded markdown resources.
/// </summary>
public class DocumentationNodeProvider : IStaticNodeProvider
{
    public const string RootNamespace = "Doc";

    private readonly Lazy<MeshNode[]> _lazyNodes;

    /// <summary>
    /// Takes <see cref="IServiceProvider"/> instead of <see cref="IMessageHub"/> so the
    /// provider can be resolved as a singleton from inside the mesh-hub-construction
    /// pipeline (DI graph: <c>WithMeshNodes()</c> → <c>GetServices&lt;IStaticNodeProvider&gt;()</c>
    /// runs while <c>IMessageHub</c> is still in-flight). Resolving <c>IMessageHub</c> here
    /// would re-enter <c>BuildHub</c> and stack-overflow. The hub is resolved lazily on
    /// first <see cref="GetStaticNodes"/> call — at which point construction is complete.
    /// </summary>
    public DocumentationNodeProvider(IServiceProvider serviceProvider)
    {
        _lazyNodes = new Lazy<MeshNode[]>(() =>
            LoadNodes(serviceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions));
    }

    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // Read-only policy for the Doc namespace — all documentation is unmodifiable
        yield return new MeshNode("_Policy", RootNamespace)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            Content = new PartitionAccessPolicy
            {
                Create = false,
                Update = false,
                Delete = false,
                Comment = true,
                Thread = true
            }
        };

        // Read access for both authenticated users (Public) and unauthenticated
        // visitors (Anonymous). Namespace MUST end in /_Access and MainNode MUST
        // point at the partition root; SecurityService silently drops assignments
        // that don't follow that shape (see feedback_access_assignment_namespace).
        yield return new MeshNode($"{WellKnownUsers.Public}_Access", $"{RootNamespace}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{WellKnownUsers.Public} Access",
            MainNode = RootNamespace,
            Content = new AccessAssignment
            {
                AccessObject = WellKnownUsers.Public,
                DisplayName = "All authenticated users",
                Roles = [new RoleAssignment { Role = "Viewer" }]
            }
        };

        yield return new MeshNode($"{WellKnownUsers.Anonymous}_Access", $"{RootNamespace}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{WellKnownUsers.Anonymous} Access",
            MainNode = RootNamespace,
            Content = new AccessAssignment
            {
                AccessObject = WellKnownUsers.Anonymous,
                DisplayName = "Unauthenticated visitors",
                Roles = [new RoleAssignment { Role = "Viewer" }]
            }
        };

        foreach (var node in _lazyNodes.Value)
            yield return node;
    }

    private static MeshNode[] LoadNodes(JsonSerializerOptions jsonOptions)
    {
        var assembly = typeof(DocumentationNodeProvider).Assembly;
        var prefix = $"{assembly.GetName().Name}.Data.";
        var parserRegistry = new FileFormatParserRegistry(jsonOptions);

        var nodes = new List<MeshNode>();

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix))
                     .Order())
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            var relativePath = ResourceNameToPath(resourceName, prefix);
            var extension = Path.GetExtension(relativePath);

            // Use the same parsers as the file system provider
            var node = parserRegistry.TryParse(extension, relativePath, content,
                $"{RootNamespace}/{relativePath}");

            if (node != null)
                nodes.Add(node);
        }

        return nodes.ToArray();
    }

    /// <summary>
    /// Parses the embedded documentation into MeshNodes whose <see cref="MeshNode.Namespace"/>,
    /// <see cref="MeshNode.Id"/>, and Path match exactly what
    /// <see cref="MeshWeaver.Hosting.Persistence.EmbeddedResourceStorageAdapter"/> serves at
    /// runtime (full path = <c>Doc/&lt;folder&gt;/&lt;file&gt;</c>, path-source-of-truth, no
    /// <c>index→folder</c> collapse). Used by the out-of-process database-migration backfill to
    /// mirror docs into Postgres for full-text + vector search, so search-result links resolve to
    /// the same path the embedded partition reads. Returns content pages only (no governance nodes).
    /// </summary>
    public static IReadOnlyList<MeshNode> LoadIndexableNodes(JsonSerializerOptions? jsonOptions = null)
    {
        var assembly = typeof(DocumentationNodeProvider).Assembly;
        var prefix = $"{assembly.GetName().Name}.Data.";
        var parserRegistry = new FileFormatParserRegistry(jsonOptions ?? new JsonSerializerOptions());

        var nodes = new List<MeshNode>();
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(prefix))
                     .Order())
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            var relativePath = ResourceNameToPath(resourceName, prefix);
            var extension = Path.GetExtension(relativePath);
            var withoutExt = relativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? relativePath[..^extension.Length]
                : relativePath;
            var fullPath = $"{RootNamespace}/{withoutExt}".Replace('\\', '/').Trim('/');

            var node = parserRegistry.TryParse(extension, relativePath, content, fullPath);
            if (node == null) continue;

            // Align Namespace/Id with the served path (mirrors EmbeddedResourceStorageAdapter's
            // path normalization, including the partition prefix and NO index→parent collapse).
            var lastSlash = fullPath.LastIndexOf('/');
            node = lastSlash > 0
                ? node with { Namespace = fullPath[..lastSlash], Id = fullPath[(lastSlash + 1)..] }
                : node with { Namespace = "", Id = fullPath };
            nodes.Add(node);
        }
        return nodes;
    }

    /// <summary>
    /// Converts embedded resource name back to file path relative to Data/.
    /// E.g. "MeshWeaver.Documentation.Data.AI.AgenticAI.md" → "AI/AgenticAI.md"
    /// </summary>
    private static string ResourceNameToPath(string resourceName, string prefix)
    {
        var withoutPrefix = resourceName[prefix.Length..];
        var lastDot = withoutPrefix.LastIndexOf('.');
        if (lastDot > 0)
        {
            var nameWithoutExt = withoutPrefix[..lastDot].Replace('.', '/');
            var ext = withoutPrefix[lastDot..];
            return nameWithoutExt + ext;
        }
        return withoutPrefix.Replace('.', '/');
    }
}
