namespace MeshWeaver.Mesh.Security;

/// <summary>
/// A registered MeshWeaver installation — the logical App ID one portal uses to identify itself to
/// another. Every user may register an instance from Settings; registration issues it an
/// <b>instance key</b> (the credential it presents as <c>Authorization: Bearer</c>).
///
/// <para>🚨 An instance key is NOT a user credential. It authenticates a *deployment*, carries no
/// user identity and no roles, and is honoured ONLY on the instance-facing surfaces (today: the
/// plugin registry's <c>/api/plugins</c>). This is deliberately a separate node type from
/// <see cref="ApiToken"/> — reusing the personal-token *mechanism* (random bytes → SHA-256 hash →
/// a routing index; never persisting the raw key) but not the personal-token *type*, so an instance
/// key can never be replayed as its owner against the general mesh API.</para>
///
/// <para>Registering an instance grants it NOTHING. What it may pull is decided separately by a
/// platform admin in <see cref="PluginGrant"/> nodes under the Admin partition, which the instance's
/// owner cannot write. Self-service registration plus admin-controlled grants is what keeps the
/// registry from serving its private sources to whoever asks.</para>
/// </summary>
public record MeshWeaverInstance
{
    /// <summary>Stable logical App ID, e.g. <c>atioz</c>. Lowercase slug, unique across the mesh —
    /// it is the identity grants are written against, so it must not be reused after deletion.</summary>
    public string InstanceId { get; init; } = "";

    /// <summary>Human-readable name shown in Settings and in the admin grant list.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>What this installation is, in the owner's words. Free text.</summary>
    public string Description { get; init; } = "";

    /// <summary>The installation's base URL (e.g. <c>https://atioz.meshweaver.cloud</c>). Advisory —
    /// used for display and support, never for authentication.</summary>
    public string HomeUrl { get; init; } = "";

    /// <summary>ObjectId of the user who registered it (matches <c>AccessContext.ObjectId</c>).</summary>
    public string OwnerUserId { get; init; } = "";

    /// <summary>Display name of the registering user.</summary>
    public string OwnerUserName { get; init; } = "";

    /// <summary>Email of the registering user.</summary>
    public string OwnerUserEmail { get; init; } = "";

    /// <summary>SHA-256 hex hash of the issued instance key. The raw key is shown once at
    /// registration and never persisted.</summary>
    public string KeyHash { get; init; } = "";

    /// <summary>When the current key was issued. Re-issuing replaces <see cref="KeyHash"/>.</summary>
    public DateTimeOffset? KeyIssuedAt { get; init; }

    /// <summary>When the instance was registered.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last time this instance authenticated. Stamped on the same single-flight/freshness
    /// discipline as <c>ApiToken.LastUsedAt</c> so a busy instance does not write on every call.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>Owner- or admin-set kill switch. A disabled instance fails authentication even
    /// while its grants remain, so access can be cut without losing the grant history.</summary>
    public bool IsDisabled { get; init; }
}

/// <summary>
/// Routing index from an instance key's hash to the instance node that owns it, mirroring
/// <see cref="ApiTokenIndex"/>: the presented key is hashed, its prefix routes to
/// <c>MeshWeaverInstance/{hashPrefix}</c>, and this record points at the real node in the owner's
/// partition. Written under System identity — the global index namespace is not user-writable.
/// </summary>
public record MeshWeaverInstanceIndex
{
    /// <summary>SHA-256 hex hash of the instance key.</summary>
    public string KeyHash { get; init; } = "";

    /// <summary>Full path of the <see cref="MeshWeaverInstance"/> node this key belongs to.</summary>
    public string InstancePath { get; init; } = "";

    /// <summary>The instance's logical App ID — lets the registry resolve grants without a second
    /// read when the instance node is already in hand.</summary>
    public string InstanceId { get; init; } = "";
}
