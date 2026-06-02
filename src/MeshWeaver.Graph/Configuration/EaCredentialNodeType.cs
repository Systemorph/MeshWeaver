using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Configuration for <b>EaCredential</b> nodes — a user's encrypted, delegated Microsoft Graph refresh
/// token for the Executive Assistant (one per user, acquired via just-in-time consent). System-managed:
/// excluded from search/create autocomplete; written only by the EA consent callback.
/// </summary>
public static class EaCredentialNodeType
{
    /// <summary>The NodeType value used to identify EA-credential nodes.</summary>
    public const string NodeType = "EaCredential";

    /// <summary>Per-user namespace segment: <c>{username}/_EaCredential</c>.</summary>
    public const string UserSegment = "_EaCredential";

    /// <summary>Registers the built-in "EaCredential" MeshNode on the mesh builder.</summary>
    public static TBuilder AddEaCredentialType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the EaCredential node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "EA Credential",
        Icon = "/static/NodeTypeIcons/key.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<EaCredential>())
    };
}
