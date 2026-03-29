using System.Reflection;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Documentation;

/// <summary>
/// Provides MeshWeaver platform documentation as static MeshNodes
/// loaded from embedded markdown resources.
/// </summary>
public class DocumentationNodeProvider : IStaticNodeProvider
{
    public const string RootNamespace = "Doc";

    private readonly Lazy<MeshNode[]> _lazyNodes;

    public DocumentationNodeProvider(IMessageHub hub)
    {
        _lazyNodes = new Lazy<MeshNode[]>(() => LoadNodes(hub.JsonSerializerOptions));
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

        // Grant all authenticated users read access to documentation
        yield return new MeshNode($"{WellKnownUsers.Public}_Access", RootNamespace)
        {
            NodeType = "AccessAssignment",
            Name = $"{WellKnownUsers.Public} Access",
            Content = new AccessAssignment
            {
                AccessObject = WellKnownUsers.Public,
                DisplayName = "All authenticated users",
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
            var node = parserRegistry.TryParseAsync(extension, relativePath, content,
                $"{RootNamespace}/{relativePath}", default).GetAwaiter().GetResult();

            if (node != null)
                nodes.Add(node);
        }

        return nodes.ToArray();
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
