using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Registers the <see cref="LicenseContent"/> node type — the license catalog at
/// <c>License/{SpdxId}</c>, plus the per-user <see cref="LicenseAcceptance"/> record.
///
/// <para>Licenses are WORLD-READABLE by design: terms nobody can read before installing are not
/// terms, they are a surprise. The partition is read-only to every non-System principal (the
/// catalog is shipped, not user-authored), the same shape the agent and skill catalogs use.</para>
/// </summary>
public static class LicenseNodeType
{
    /// <summary>The node-type identifier for license nodes.</summary>
    public const string NodeType = "License";

    /// <summary>The node-type identifier for a recorded acceptance.</summary>
    public const string AcceptanceNodeType = "LicenseAcceptance";

    /// <summary>Namespace holding a user's acceptances, inside their own partition — the evidence
    /// belongs to the user who gave it.</summary>
    public const string AcceptanceNamespace = "_LicenseAcceptance";

    /// <summary>Path of a user's acceptance record for one package.</summary>
    public static string AcceptancePath(string userId, string packageId) =>
        $"{userId}/{AcceptanceNamespace}/{packageId}";

    /// <summary>Registers the license catalog + acceptance types on the mesh builder.</summary>
    public static TBuilder AddLicenseType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateLicenseMeshNode(), CreateAcceptanceMeshNode(), CreatePolicy());
        // A license is reference material, not pickable content for a composer.
        builder.AddAutocompleteExcludedTypes(NodeType, AcceptanceNodeType);
        builder.ConfigureHub(config => config
            .WithType<LicenseContent>(nameof(LicenseContent))
            .WithType<LicenseAcceptance>(nameof(LicenseAcceptance)));
        return builder;
    }

    /// <summary>The license node definition.</summary>
    public static MeshNode CreateLicenseMeshNode() => new(NodeType)
    {
        Name = "License",
        IsSatelliteType = false,
        HubConfiguration = config => config
            .AddDefaultLayoutAreas()
            .AddMeshDataSource(source => source.WithContentType<LicenseContent>())
    };

    /// <summary>The acceptance node definition.</summary>
    public static MeshNode CreateAcceptanceMeshNode() => new(AcceptanceNodeType)
    {
        Name = "License Acceptance",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source.WithContentType<LicenseAcceptance>())
    };

    // World-readable, non-writable: everyone must be able to read the terms BEFORE accepting them,
    // and nobody but System may author the shipped catalog.
    private static MeshNode CreatePolicy() =>
        new("_Policy", WellKnownLicenses.Partition)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            Content = new PartitionAccessPolicy
            {
                PublicRead = true,
                Create = false,
                Update = false,
                Delete = false,
                Comment = false,
                Thread = false
            }
        };
}
