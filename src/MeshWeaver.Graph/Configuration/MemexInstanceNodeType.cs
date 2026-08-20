using System.ComponentModel;
using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// One remote mesh this client connects to — the content of a <c>MemexInstance</c> node. Stored as a
/// mesh node (not device preferences), so "connect to other meshes" is managed as ordinary nodes that
/// the bootstrap reads with <c>GetQuery</c> and then dials with <c>ConnectToMesh</c>. Applies to any
/// mesh: connecting to other meshes is a capability every mesh exposes, the local client is just the
/// first consumer.
/// </summary>
public record MemexInstanceContent
{
    /// <summary>Human label (e.g. "memex").</summary>
    public string? DisplayName { get; init; }

    /// <summary>The portal base URL; the SignalR endpoint is <c>{Url}/signalr</c>.</summary>
    public string Url { get; init; } = "";

    /// <summary>API token authenticating this client to the remote mesh. Null/empty = not yet joined.
    /// Credential material — hidden from the default editor forms.</summary>
    [Browsable(false)]
    public string? Token { get; init; }

    /// <summary>
    /// The remote mesh's address id — keys the SignalR connection and routes <c>{meshId}/…</c> targets
    /// to it (the multi-remote route discriminator). Defaults to the instance id when unset.
    /// </summary>
    public string? MeshId { get; init; }

    /// <summary>Last time the client connected to this mesh (stamped on connect).</summary>
    public DateTime? LastConnected { get; init; }

    /// <summary>OAuth refresh token from a browser sign-in; null for pasted API tokens.
    /// Credential material — hidden from the default editor forms.</summary>
    [Browsable(false)]
    public string? RefreshToken { get; init; }

    /// <summary>OAuth client id the client registered at this portal (dynamic client registration).</summary>
    [Browsable(false)]
    public string? ClientId { get; init; }

    /// <summary>When the OAuth access token expires; null for pasted API tokens.</summary>
    public DateTime? TokenExpiresAt { get; init; }

    /// <summary>True when a token is present, so the client should join this mesh.</summary>
    public bool IsAuthenticated => !string.IsNullOrEmpty(Token);
}

/// <summary>
/// The <c>MemexInstance</c> node type — one node per remote mesh the client connects to. The bootstrap
/// reads these via <c>workspace.GetQuery(… nodeType:MemexInstance)</c> and connects each authenticated
/// one with <c>hub.ConnectToMesh(url, token, remoteAddress)</c>.
/// </summary>
public static class MemexInstanceNodeType
{
    /// <summary>The NodeType value used to identify connectable-mesh nodes.</summary>
    public const string NodeType = "MemexInstance";

    /// <summary>The container segment instances live under: <c>Instance/{id}</c>.</summary>
    public const string Segment = "Instance";

    /// <summary>The node path for an instance: <c>Instance/{id}</c>.</summary>
    public static string PathFor(string id) => $"{Segment}/{id}";

    /// <summary>Registers the built-in "MemexInstance" MeshNode on the mesh builder.</summary>
    public static TBuilder AddMemexInstanceType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        // The content type must resolve on EVERY hub that materializes these nodes — the per-node
        // hub gets it via WithContentType below, but node-stream/query hubs (MeshNodeStreamCache)
        // otherwise degrade the content to an untyped JsonElement ("stayed an untyped JsonElement"
        // boot warning; typed ContentAs<MemexInstanceContent> consumers then read null). Mesh-level
        // registry, so all hubs inherit it (the UiContributionNodeType pattern).
        builder.WithMeshType(typeof(MemexInstanceContent), nameof(MemexInstanceContent));
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>Creates a MeshNode definition for the MemexInstance node type.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Memex Instance",
        Icon = "/static/NodeTypeIcons/box.svg",
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<MemexInstanceContent>())
    };
}
