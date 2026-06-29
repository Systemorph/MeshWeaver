using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Built-in import script templates seeded as <see cref="MeshNode"/>s of type
/// <c>Code</c>. The <c>.csx</c> bodies travel inside this assembly as embedded
/// resources (<c>Templates/NodeCopy.csx</c>, <c>Templates/Mirror.csx</c>);
/// each one becomes an executable Code node at <c>Templates/Import/{op}</c>
/// that the kernel runs when an <see cref="ExecuteScriptRequest"/> arrives
/// carrying caller-supplied <see cref="ExecuteScriptRequest.Inputs"/>.
///
/// <para>Stateless static helper — registered via
/// <c>builder.AddMeshNodes(...)</c> in <c>AddGraph</c> rather than as an
/// <c>IStaticNodeProvider</c> through DI, since it holds no state. See
/// <c>Doc/Architecture/AsynchronousCalls.md</c> → "Static handlers compose —
/// don't wrap them in a service for 'DI cleanliness'".</para>
///
/// <para>Canonical "operations as scripts" shape — see
/// <c>Doc/Architecture/ActivityControlPlane.md</c> → "Operations as scripts".</para>
/// </summary>
public static class GraphImportTemplates
{
    /// <summary>Namespace the import-template Code nodes are seeded under.</summary>
    public const string TemplatesNamespace = "Templates/Import";
    /// <summary>Node id of the "copy node tree" import template.</summary>
    public const string NodeCopyId = "NodeCopy";
    /// <summary>Node id of the "mirror across portals" import template.</summary>
    public const string MirrorId = "Mirror";

    /// <summary>
    /// Returns the built-in import-template Code nodes (lazily loaded from this
    /// assembly's embedded .csx resources) for registration via AddMeshNodes.
    /// </summary>
    /// <returns>The set of executable Code template nodes.</returns>
    public static IEnumerable<MeshNode> GetStaticNodes() => LazyNodes.Value;

    private static readonly Lazy<MeshNode[]> LazyNodes = new(LoadAllNodes);

    private static MeshNode[] LoadAllNodes()
    {
        var copyCode = ReadEmbeddedResource("Templates.NodeCopy.csx");
        var mirrorCode = ReadEmbeddedResource("Templates.Mirror.csx");

        return
        [
            BuildCodeNode(
                NodeCopyId,
                "Copy node tree",
                "Deep-copies a node and its descendants under a target namespace. Triggered by ExecuteScriptRequest with Inputs.sourcePath + Inputs.targetNamespace.",
                copyCode),
            BuildCodeNode(
                MirrorId,
                "Mirror across portals",
                "Push or pull a subtree across MeshWeaver portals over MCP-HTTP. Triggered by ExecuteScriptRequest with Inputs.remoteBaseUrl + Inputs.remoteToken + Inputs.sourcePath + Inputs.direction.",
                mirrorCode),
        ];
    }

    private static MeshNode BuildCodeNode(string id, string name, string description, string code) =>
        new(id, TemplatesNamespace)
        {
            NodeType = "Code",
            Name = name,
            Description = description,
            State = MeshNodeState.Active,
            Content = new CodeConfiguration
            {
                Code = code,
                Language = "csharp",
                IsExecutable = true,
                // Each run lands in the caller's home — imports are user-scoped.
                ActivityParentPath = "{viewer}"
            }
        };

    private static string ReadEmbeddedResource(string relativeName)
    {
        var assembly = typeof(GraphImportTemplates).Assembly;
        var prefix = $"{assembly.GetName().Name}.";
        var fullName = prefix + relativeName;
        using var stream = assembly.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{fullName}' not found. Ensure it is included as <EmbeddedResource> in the .csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
