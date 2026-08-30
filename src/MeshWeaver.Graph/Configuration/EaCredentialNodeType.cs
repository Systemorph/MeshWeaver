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
        // 🚨 The discriminator has to be known to EVERY hub, not only to the per-node hub the
        // data source above configures (MeshWeaver#2729). A reader elsewhere in the mesh whose
        // TypeRegistry lacks it gets a raw JsonElement and therefore a SILENT null: the value
        // renders empty and reactive waits time out, with no exception anywhere to grep for.
        builder.ConfigureHub(config => config.WithType<EaCredential>(nameof(EaCredential)));
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the EaCredential node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "EA Credential",
        Icon = "/static/NodeTypeIcons/key.svg",
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<EaCredential>())
    };
}
