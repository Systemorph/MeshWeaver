using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>A portal endpoint the client can show / connect to (name + URL).</summary>
public record MemexClientSite(string Name, string Url);

/// <summary>
/// Config for one Memex <b>client installation</b> — the content of a <c>MemexClient</c> node at
/// <c>{user}/Client/{installationId}</c>. Stored in the mesh (not on the device) so the user manages
/// every client/installation centrally from the portal. The device keeps only the minimal bootstrap
/// (installation id + the first portal URL + token) needed to connect and read this node.
/// </summary>
public record MemexClientContent
{
    /// <summary>Human label for this installation (e.g. "Roland's iPhone").</summary>
    public string? DisplayName { get; init; }

    /// <summary>Platform tag (ios / android / windows / maccatalyst), set by the client.</summary>
    public string? Platform { get; init; }

    /// <summary>The portals this installation shows / can switch between.</summary>
    public ImmutableList<MemexClientSite> Sites { get; init; } = ImmutableList<MemexClientSite>.Empty;

    /// <summary>Namespace new voice threads are created under (defaults to the user's home).</summary>
    public string? DefaultNamespace { get; init; }

    /// <summary>Voice on/off and the wake word that starts capture.</summary>
    public bool VoiceEnabled { get; init; } = true;
    /// <summary>The spoken wake word that starts voice capture (defaults to "memex").</summary>
    public string WakeWord { get; init; } = "memex";

    /// <summary>Last time this installation checked in (the client stamps it on connect).</summary>
    public DateTime? LastSeen { get; init; }
}

/// <summary>
/// The <c>MemexClient</c> node type — per-installation client configuration as a mesh node.
/// User-creatable under their own namespace (<c>{user}/Client/{installationId}</c>), so it stays in
/// search/create contexts and is editable from the portal like any other node.
/// </summary>
public static class MemexClientNodeType
{
    /// <summary>The NodeType value used to identify client-installation nodes.</summary>
    public const string NodeType = "MemexClient";

    /// <summary>Per-user namespace segment: <c>{user}/Client</c>.</summary>
    public const string ClientSegment = "Client";

    /// <summary>The node path for an installation: <c>{user}/Client/{installationId}</c>.</summary>
    public static string PathFor(string user, string installationId) => $"{user}/{ClientSegment}/{installationId}";

    /// <summary>Registers the built-in "MemexClient" MeshNode on the mesh builder.</summary>
    public static TBuilder AddMemexClientType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the MemexClient node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Memex Client",
        Icon = "/static/NodeTypeIcons/box.svg",
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<MemexClientContent>())
    };
}
