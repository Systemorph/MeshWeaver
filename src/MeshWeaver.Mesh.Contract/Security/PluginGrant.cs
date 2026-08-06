namespace MeshWeaver.Mesh.Security;

/// <summary>
/// What a registered <see cref="MeshWeaverInstance"/> is allowed to pull from this registry.
///
/// <para>🚨 Grant nodes live in the <b>Admin</b> partition (<c>Admin/_PluginGrant/{instanceId}</c>),
/// NOT in the owner's partition. Registration is self-service — any user may register an instance —
/// so the grant must be written somewhere the owner cannot reach, or self-service registration would
/// simply be self-service access to every private source. Only a platform admin
/// (<c>hub.IsGlobalAdmin()</c>) writes these.</para>
///
/// <para>An instance with no grant node, or an empty <see cref="Entries"/> list, gets <b>nothing</b>.
/// Registering is identity, not entitlement.</para>
/// </summary>
public record PluginGrant
{
    /// <summary>The <see cref="MeshWeaverInstance.InstanceId"/> these grants apply to.</summary>
    public string InstanceId { get; init; } = "";

    /// <summary>What the instance may pull. Empty = nothing.</summary>
    public IReadOnlyCollection<PluginGrantEntry> Entries { get; init; } = [];

    /// <summary>ObjectId of the platform admin who last changed the grant — grants are an
    /// access decision, so who made it is part of the record.</summary>
    public string GrantedByUserId { get; init; } = "";

    /// <summary>When the grant was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Whether this grant permits <paramref name="packageId"/> from registry source
    /// <paramref name="sourceName"/>. Both an exact package grant and a whole-source grant
    /// (<see cref="PluginGrantEntry.AllPackages"/>) satisfy the check.
    /// </summary>
    public bool Allows(string sourceName, string packageId) =>
        Entries.Any(e => e.Matches(sourceName, packageId));
}

/// <summary>
/// One authorization: a registry source, and either a single package within it or every package
/// in it. The two levels exist because both are real — a partner instance may be entitled to one
/// plugin out of a repo (<c>Reinsurance/UWDeepfield</c>) while our own instance carries the whole
/// platform repo (<c>Plugins/*</c>).
/// </summary>
public record PluginGrantEntry
{
    /// <summary><see cref="PackageId"/> value meaning "every package in this source".</summary>
    public const string AllPackages = "*";

    /// <summary>The registry source's configured <c>Name</c> (e.g. <c>Plugins</c>,
    /// <c>Education</c>, <c>Reinsurance</c>). Matched case-insensitively — a source name is an
    /// operator-typed config value, not a wire identifier.</summary>
    public string Source { get; init; } = "";

    /// <summary>The package id, or <see cref="AllPackages"/> for the whole source. Package ids are
    /// matched with ordinal case sensitivity, exactly as the catalog compares them.</summary>
    public string PackageId { get; init; } = AllPackages;

    /// <summary>Whether this entry authorizes <paramref name="packageId"/> from
    /// <paramref name="sourceName"/>.</summary>
    public bool Matches(string sourceName, string packageId) =>
        string.Equals(Source, sourceName, StringComparison.OrdinalIgnoreCase)
        && (PackageId == AllPackages || string.Equals(PackageId, packageId, StringComparison.Ordinal));

    /// <summary>Renders the entry the way it is written in docs and the admin UI —
    /// <c>Plugins/*</c>, <c>Reinsurance/UWDeepfield</c>.</summary>
    public override string ToString() => $"{Source}/{PackageId}";
}
