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
/// <para>🚨 This IS the sync licence. Its entries carry the terms the right was issued under —
/// <see cref="PluginGrantEntry.IssuedUnderLicense"/>, <see cref="PluginGrantEntry.ExpiresAt"/>,
/// <see cref="PluginGrantEntry.IssuedVia"/> — so "may this instance replicate this package" and
/// "under what licence, until when, on whose authority" are ONE record rather than an ACL beside a
/// licence that could disagree with it. The subject is a <see cref="MeshWeaverInstance"/> and the
/// right is SYNC; it is deliberately NOT the user-facing entitlement, which grants a person the use
/// of a package on their own portal and says nothing about a deployment holding a copy.</para>
///
/// <para>An instance with no grant node, or an empty <see cref="Entries"/> list, gets <b>nothing</b>.
/// Registering is identity, not entitlement. The one qualification: a registry operator may opt
/// specific sources into every new registration via <c>PluginCatalog:DefaultGrants</c> (e.g. the
/// platform's own <c>Plugins/*</c>) — registration then SEEDS those entries into this node, so the
/// grant node stays the single authority and an admin can still revoke per instance. Private/paid
/// sources are never defaulted; they remain admin-granted.</para>
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
    /// Kill switch for the whole grant. A revoked grant authorizes nothing while its entries — and
    /// the licence terms recorded on them — stay intact, so revoking never destroys the record of
    /// what was licensed and why. Revoking a SINGLE package is done by letting that entry expire or
    /// removing it; this is the instance-wide stop.
    ///
    /// <para>Distinct from <c>MeshWeaverInstance.IsDisabled</c>, which stops the instance
    /// authenticating at all. An instance may legitimately remain live while its sync licence is
    /// revoked.</para>
    /// </summary>
    public bool IsRevoked { get; init; }

    /// <summary>
    /// Whether this grant permits <paramref name="packageId"/> from registry source
    /// <paramref name="sourceName"/> at <paramref name="now"/>, decided WITHOUT knowing the
    /// package's tier. Both an exact package grant and a whole-source grant
    /// (<see cref="PluginGrantEntry.AllPackages"/>) satisfy the match, and the matching entry must
    /// still be within its term.
    ///
    /// <para>🚨 Only PLAN-LESS entries can answer here. An entry scoped to a plan
    /// (<see cref="PluginGrantEntry.Tier"/>) licenses packages BY THEIR TIER, and a caller that does
    /// not know the package's tier cannot be told "yes" by it — that would turn every plan into an
    /// all-access grant at exactly the call sites that forgot to ask. Registry surfaces pass the
    /// tier and the ladder (<see cref="Allows(string,string,string?,PlanTierRanks,DateTimeOffset)"/>).</para>
    ///
    /// <para>🚨 <paramref name="now"/> is an ARGUMENT, never read from the ambient clock inside the
    /// predicate. This is the authorization decision of the registry surface: it has to be
    /// reproducible in a test at a chosen instant, and an expiry that can only be exercised by
    /// waiting is an expiry nobody pins. The convenience overload supplies <c>UtcNow</c> for the
    /// call sites that legitimately mean "right now".</para>
    /// </summary>
    public bool Allows(string sourceName, string packageId, DateTimeOffset now) =>
        !IsRevoked && Entries.Any(e =>
            !e.IsPlanScoped && e.Matches(sourceName, packageId) && e.IsValidAt(now));

    /// <summary>
    /// <see cref="Allows(string,string,DateTimeOffset)"/> evaluated at the current instant — what a
    /// live request means. Prefer the explicit-instant overload in tests.
    /// </summary>
    public bool Allows(string sourceName, string packageId) =>
        Allows(sourceName, packageId, DateTimeOffset.UtcNow);

    /// <summary>
    /// Whether this grant permits <paramref name="packageId"/> — declaring
    /// <paramref name="packageTier"/> — from registry source <paramref name="sourceName"/> at
    /// <paramref name="now"/>: some entry matches the pair, is within its term, and COVERS the
    /// package's tier under <paramref name="ranks"/> (<see cref="PlanTierRanks.Covers"/>). A plan-less
    /// entry covers every tier; a plan-scoped one covers what its plan ranks at or above it. This is
    /// the overload every registry surface decides with.
    /// </summary>
    public bool Allows(
        string sourceName, string packageId, string? packageTier, PlanTierRanks ranks, DateTimeOffset now) =>
        !IsRevoked && Entries.Any(e =>
            e.Matches(sourceName, packageId) && e.IsValidAt(now) && e.Covers(packageTier, ranks));

    /// <summary><see cref="Allows(string,string,string?,PlanTierRanks,DateTimeOffset)"/> at the
    /// current instant.</summary>
    public bool Allows(string sourceName, string packageId, string? packageTier, PlanTierRanks ranks) =>
        Allows(sourceName, packageId, packageTier, ranks, DateTimeOffset.UtcNow);

    /// <summary>
    /// Whether this grant carries a live, PLAN-LESS whole-source entry for
    /// <paramref name="sourceName"/> — what fetching a source's sealed publication as a whole
    /// requires. A plan-scoped <c>Source/*</c> covers the packages of its plan one by one and never
    /// the source's whole publication, which carries every plan's bundles.
    /// </summary>
    public bool AllowsWholeSource(string sourceName, DateTimeOffset now) =>
        !IsRevoked && Entries.Any(e =>
            !e.IsPlanScoped
            && e.PackageId == PluginGrantEntry.AllPackages
            && string.Equals(e.Source, sourceName, StringComparison.OrdinalIgnoreCase)
            && e.IsValidAt(now));

    /// <summary><see cref="AllowsWholeSource(string,DateTimeOffset)"/> at the current instant.</summary>
    public bool AllowsWholeSource(string sourceName) => AllowsWholeSource(sourceName, DateTimeOffset.UtcNow);
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

    /// <summary>
    /// The subscription PLAN this entry is scoped to (<c>personal</c>, <c>pro</c>,
    /// <c>dedicated</c>, <c>enterprise</c> — the ids of the registry's <c>Admin/Tiers/*</c> nodes),
    /// or null for every tier.
    ///
    /// <para>A plan-scoped entry licenses the packages of its source BY TIER: a package declaring
    /// a tier ranked at or below the plan's, a baseline package declaring none, and — on an
    /// all-access plan — everything. That is what lets one <c>Plugins/*@personal</c> entry express
    /// "the platform repo, as far as the Personal plan reaches" instead of an admin enumerating
    /// packages, and it is decided by <see cref="PlanTierRanks.Covers"/> against the ladder the
    /// registry reads from its tier nodes. Null keeps today's meaning exactly: the whole source,
    /// whatever tier its packages declare.</para>
    /// </summary>
    public string? Tier { get; init; }

    /// <summary>Whether this entry is scoped to a plan (<see cref="Tier"/> set).</summary>
    public bool IsPlanScoped => !string.IsNullOrWhiteSpace(Tier);

    /// <summary>
    /// End of this entry's term. Null = perpetual (revocation remains available). A licence that
    /// ends is the normal commercial case, and it is recorded PER ENTRY because an instance
    /// routinely holds several licences with different terms — a perpetual grant on the platform
    /// repo alongside a one-year licence for a paid package.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// SPDX id of the licence this sync right was issued under (<c>Apache-2.0</c>, or a commercial
    /// id in the <c>License/</c> catalog). Null = unspecified, and it must STAY null rather than
    /// defaulting: recording terms nobody granted is worse than recording none. Resolves to a
    /// <c>LicenseContent</c> node, which is what lets the terms actually be shown.
    /// </summary>
    public string? IssuedUnderLicense { get; init; }

    /// <summary>
    /// How this entry came to exist, in the issuer's words — an order id, a coupon code, a support
    /// ticket, <c>DefaultGrants</c> for the registration seed. Free text and advisory: it is the
    /// audit trail for a right that is otherwise indistinguishable from any other.
    /// </summary>
    public string? IssuedVia { get; init; }

    /// <summary>When the entry was issued. Default (<c>MinValue</c>) on entries written before
    /// licence terms existed, which is why <see cref="IsValidAt"/> never reads it.</summary>
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>
    /// Whether the entry is within its term at <paramref name="now"/>. An entry with no
    /// <see cref="ExpiresAt"/> is always within term — which is what makes every grant written
    /// before this field existed keep working unchanged.
    /// </summary>
    public bool IsValidAt(DateTimeOffset now) => ExpiresAt is null || now < ExpiresAt;

    /// <summary>Whether this entry authorizes <paramref name="packageId"/> from
    /// <paramref name="sourceName"/>. Match only — the term is checked by
    /// <see cref="IsValidAt"/>, so a caller can tell "not licensed" from "licence expired".</summary>
    public bool Matches(string sourceName, string packageId) =>
        string.Equals(Source, sourceName, StringComparison.OrdinalIgnoreCase)
        && (PackageId == AllPackages || string.Equals(PackageId, packageId, StringComparison.Ordinal));

    /// <summary>Whether this entry's plan covers a package declaring <paramref name="packageTier"/>
    /// under <paramref name="ranks"/> — always true for a plan-less entry. Coverage only; the pair
    /// match is <see cref="Matches"/> and the term is <see cref="IsValidAt"/>.</summary>
    public bool Covers(string? packageTier, PlanTierRanks ranks) => ranks.Covers(Tier, packageTier);

    /// <summary>Renders the entry the way it is written in docs and the admin UI —
    /// <c>Plugins/*</c>, <c>Reinsurance/UWDeepfield</c>, <c>Plugins/*@pro</c> for a plan-scoped one.</summary>
    public override string ToString() =>
        IsPlanScoped ? $"{Source}/{PackageId}@{PlanTierRanks.Canonical(Tier)}" : $"{Source}/{PackageId}";

    /// <summary>
    /// Parses the <c>Source/Package[@plan]</c> notation used in config and docs — <c>Plugins/*</c>,
    /// <c>Reinsurance/UWDeepfield</c>, a bare <c>Plugins</c> meaning the whole source, or
    /// <c>Plugins/*@personal</c> for an entry scoped to a plan. Returns <c>null</c> for a blank or
    /// malformed value (no source, nothing after the separator, an empty plan after <c>@</c>)
    /// instead of throwing: these are operator-typed config values, and one bad list entry must
    /// not take the registration surface down with it.
    /// </summary>
    public static PluginGrantEntry? TryParse(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        string? tier = null;
        var at = trimmed.LastIndexOf('@');
        if (at >= 0)
        {
            tier = PlanTierRanks.Canonical(trimmed[(at + 1)..]);
            if (tier.Length == 0)
                return null;
            trimmed = trimmed[..at].Trim();
        }
        var slash = trimmed.IndexOf('/');
        var source = (slash < 0 ? trimmed : trimmed[..slash]).Trim();
        var packageId = slash < 0 ? AllPackages : trimmed[(slash + 1)..].Trim();
        if (source.Length == 0 || packageId.Length == 0)
            return null;
        return new PluginGrantEntry { Source = source, PackageId = packageId, Tier = tier };
    }
}
