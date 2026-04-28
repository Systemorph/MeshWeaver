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
