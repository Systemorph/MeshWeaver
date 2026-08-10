using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What THIS instance is actually running, as a single list: every module it carries, with where
/// that module came from and the ref it was last materialised at.
///
/// <para><b>Why this type exists.</b> The candidate-release gate's unit of verification is the
/// <b>combo</b> — a candidate image × the module versions THAT instance has installed — because no
/// central set of dependents is the right set (a NodeType broken on a plugin nobody deployed is not
/// an outage; broken on a plugin one portal installed, it is an outage for exactly that portal).
/// Only the instance knows its own set, and this is the read that states it.</para>
///
/// <para><b>🚨 Two shapes, not one — and a reader that handles only one looks HEALTHY while
/// returning nothing.</b> Modules reach an instance by two paths and each records its coordinate
/// differently: per-Space <see cref="GitSyncCoordinate"><c>{Space}/_GitSync</c></see> entries (how
/// most modules actually arrive) and <see cref="PackageCoordinate"><c>Plugins/{id}</c> install
/// records</see>. Measured on memex 2026-08-10: <b>42 sync configs and ZERO install records</b> —
/// so a Package-only reader reports an empty set there and reads as "nothing installed, all clear".
/// <see cref="InstanceComboReader"/> folds both, which is the whole point of the type.</para>
///
/// <para><b>🚨 Every ref in here is PROVENANCE, never proof of identity.</b> See
/// <see cref="ModuleCoordinate.ProvenanceRef"/>: it records what the module was last materialised
/// FROM, not that the live mesh still matches it. In-mesh editing is always possible, so the honest
/// claim is "this content came from that ref", never "the mesh equals that ref".</para>
///
/// <para>See <c>Doc/Architecture/CandidateReleaseProtocol</c>.</para>
/// </summary>
public sealed record InstanceCombo
{
    /// <summary>When this combo was read (UTC). A combo is a SNAPSHOT: a sync landing a second
    /// later moves a ref, so a gate must record which read it verified.</summary>
    public DateTimeOffset ReadAt { get; init; }

    /// <summary>Every module this instance carries, ordered by <see cref="ModuleCoordinate.ModuleId"/>.
    /// One entry per module even when a module arrived BOTH ways — both coordinates hang off the
    /// single entry.</summary>
    public ImmutableList<ModuleCoordinate> Modules { get; init; } = [];

    /// <summary>
    /// Modules a configured repo ships that this instance does NOT carry, read from the
    /// <see cref="ModuleDiscovery"/> records when they exist. Empty when no discovery record does —
    /// which is NOT the same as "there are none", and is why that case raises a
    /// <see cref="Caveats">caveat</see>.
    /// </summary>
    public ImmutableList<ModuleGap> NotCarried { get; init; } = [];

    /// <summary>
    /// Everything that makes this answer less than a complete, exactly-reproducible statement:
    /// a source that could not be read, an absent discovery record, a module pinned only to a
    /// moving branch, and the standing provenance caveat.
    ///
    /// <para>🚨 A gate MUST surface these. The failure mode this whole type exists to prevent is a
    /// partial answer that reads as a healthy one.</para>
    /// </summary>
    public ImmutableList<string> Caveats { get; init; } = [];

    /// <summary>
    /// False when a source query FAILED, so the module list is known to be short. Distinguishes
    /// "this instance carries nothing" from "we could not find out" — the two answers a gate must
    /// never conflate.
    /// </summary>
    public bool IsComplete { get; init; } = true;

    /// <summary>
    /// Whether every carried module names a ref that identifies an immutable tree, so the combo can
    /// be re-assembled and get the same input twice. False when any module is pinned only to a
    /// moving branch or to nothing at all — a run against those two is not repeatable and must not
    /// be reported as though it were.
    /// </summary>
    [JsonIgnore]
    public bool IsReproducible =>
        IsComplete && Modules.Count > 0 && Modules.All(m => m.Fidelity == RefFidelity.Exact);
}

/// <summary>
/// One module of an instance's combo, folded from whichever of the two recording shapes carry it.
/// The <see cref="ModuleId"/> is the mesh partition the module's content lives in — the id both
/// shapes agree on, and what makes folding them possible at all.
/// </summary>
public sealed record ModuleCoordinate
{
    /// <summary>The module's mesh partition / Space id (<c>SocialMedia</c>, <c>Store</c>, …).</summary>
    public string ModuleId { get; init; } = "";

    /// <summary>The module's display name where one is recorded.</summary>
    public string? Name { get; init; }

    /// <summary>The <c>{Space}/_GitSync</c> coordinate, when the module arrived by repo import.</summary>
    public GitSyncCoordinate? GitSync { get; init; }

    /// <summary>The <c>Plugins/{id}</c> install-record coordinate, when the module was installed by
    /// the package installer.</summary>
    public PackageCoordinate? Package { get; init; }

    /// <summary>Which shape(s) recorded this module.</summary>
    [JsonIgnore]
    public ModuleOrigin Origin => (GitSync, Package) switch
    {
        (not null, not null) => ModuleOrigin.Both,
        (not null, null) => ModuleOrigin.GitSync,
        (null, not null) => ModuleOrigin.PackageInstall,
        _ => ModuleOrigin.None,
    };

    /// <summary>
    /// The ref this module was last materialised FROM — the coordinate a gate checks out to
    /// re-assemble the combo.
    ///
    /// <para>🚨 <b>PROVENANCE, not identity.</b> It answers "where did this content come from?",
    /// never "is the live mesh byte-identical to this?". A node edited in-mesh after
    /// <see cref="MaterializedAt"/> has diverged from this ref and nothing here says otherwise —
    /// the mesh is authored as well as synced, so divergence is normal, not corruption. Treat a
    /// combo run at this ref as "the recorded input", and treat a difference against the live mesh
    /// as expected rather than as a failure.</para>
    ///
    /// <para>Preference order when both shapes recorded one: the install record's
    /// <see cref="PackageCoordinate.ModuleVersion"/> (a hash of the MODULE's own files) beats the
    /// sync entry's commit sha (whole-repo, so it moves when an unrelated module changes).</para>
    /// </summary>
    [JsonIgnore]
    public string? ProvenanceRef =>
        Coalesce(Package?.ModuleVersion)
        ?? Coalesce(GitSync?.LastSyncCommitSha)
        ?? Coalesce(Package?.InstalledFromRef)
        ?? Coalesce(GitSync?.Branch);

    /// <summary>Which record <see cref="ProvenanceRef"/> was taken from — so a caller resolving it
    /// knows whether it holds a git commit, a content hash, or a branch name.</summary>
    [JsonIgnore]
    public ProvenanceKind ProvenanceKind =>
        Coalesce(Package?.ModuleVersion) is not null ? ProvenanceKind.ModuleVersion
        : Coalesce(GitSync?.LastSyncCommitSha) is not null ? ProvenanceKind.SyncedCommit
        : Coalesce(Package?.InstalledFromRef) is not null ? ProvenanceKind.InstalledRef
        : Coalesce(GitSync?.Branch) is not null ? ProvenanceKind.Branch
        : ProvenanceKind.None;

    /// <summary>
    /// Whether <see cref="ProvenanceRef"/> identifies an immutable tree
    /// (<see cref="RefFidelity.Exact"/>) or something that moves under it
    /// (<see cref="RefFidelity.Moving"/>) — the difference between a combo two runs agree on and
    /// one they do not.
    /// </summary>
    [JsonIgnore]
    public RefFidelity Fidelity => ProvenanceKind switch
    {
        // A content hash over the module's own file set: equal hash ⇒ identical files.
        ProvenanceKind.ModuleVersion => RefFidelity.Exact,
        // A sha is exact; anything else recorded in these fields is a branch/tag that can move.
        ProvenanceKind.SyncedCommit or ProvenanceKind.InstalledRef =>
            LooksLikeCommitSha(ProvenanceRef) ? RefFidelity.Exact : RefFidelity.Moving,
        ProvenanceKind.Branch => RefFidelity.Moving,
        _ => RefFidelity.Unrecorded,
    };

    /// <summary>
    /// When this module's content was last written INTO the mesh from <see cref="ProvenanceRef"/> —
    /// the install time, else the sync's reconcile horizon. 🚨 The boundary the honesty note on
    /// <see cref="ProvenanceRef"/> turns on: anything authored in-mesh after this moment is not in
    /// the ref. Null when neither shape recorded a time.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? MaterializedAt => Package?.InstalledAtUtc ?? GitSync?.LastSyncedAt;

    /// <summary>The repository this module comes from, where a shape records one.</summary>
    [JsonIgnore]
    public string? RepositoryUrl => Coalesce(GitSync?.RepositoryUrl);

    /// <summary>A 40- or 64-character hex string — a git commit sha. Anything else in a ref field is
    /// a branch or tag, which moves.</summary>
    private static bool LooksLikeCommitSha(string? value) =>
        value is { Length: 40 or 64 } && value.All(char.IsAsciiHexDigit);

    private static string? Coalesce(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// The <c>{Space}/_GitSync</c> coordinate — how MOST modules actually reach a real instance
/// (<see cref="MeshWeaver.GitSync.GitHubSyncConfig"/> nodes; 42 of them on memex against zero
/// install records).
/// </summary>
public sealed record GitSyncCoordinate
{
    /// <summary>The sync-config node's path (<c>{Space}/_GitSync</c>, or
    /// <c>{Space}/_GitSync/{sourceId}</c> for an additional source) — where this coordinate was
    /// read, so an operator can go look at it.</summary>
    public string ConfigPath { get; init; } = "";

    /// <summary>The repository the Space syncs from.</summary>
    public string? RepositoryUrl { get; init; }

    /// <summary>The branch it commits against. 🚨 A branch MOVES — on its own it is not a
    /// coordinate; <see cref="LastSyncCommitSha"/> is.</summary>
    public string? Branch { get; init; }

    /// <summary>The module's folder inside the repo.</summary>
    public string? Subdirectory { get; init; }

    /// <summary>The commit this Space was last synced from — the exact coordinate.
    /// 🚨 Provenance: see <see cref="ModuleCoordinate.ProvenanceRef"/>.</summary>
    public string? LastSyncCommitSha { get; init; }

    /// <summary>When mesh and repo were last reconciled. A node modified after this has diverged
    /// from <see cref="LastSyncCommitSha"/>.</summary>
    public DateTimeOffset? LastSyncedAt { get; init; }
}

/// <summary>The <c>Plugins/{id}</c> install-record coordinate — a
/// <see cref="PackageManifest"/> written by <see cref="PackageInstaller"/>.</summary>
public sealed record PackageCoordinate
{
    /// <summary>The install record's node path (<c>Plugins/{id}</c>).</summary>
    public string RecordPath { get; init; } = "";

    /// <summary>The package id — the record's id, which may differ from the partition it installed
    /// into.</summary>
    public string PackageId { get; init; } = "";

    /// <summary>The package's display name, as the manifest recorded it.</summary>
    public string? Name { get; init; }

    /// <summary>The module's content version from its <c>manifest.lock</c> — a hash over the
    /// module's own files, so it is exact AND does not move when a sibling module changes.</summary>
    public string? ModuleVersion { get; init; }

    /// <summary>The package's declared version string (whole-repo commit sha for a node repo).</summary>
    public string? Version { get; init; }

    /// <summary>The git ref the install ran from (a commit or a branch).</summary>
    public string? InstalledFromRef { get; init; }

    /// <summary>When the package was installed (UTC).</summary>
    public DateTimeOffset? InstalledAtUtc { get; init; }

    /// <summary>The configured registry source the package came from.</summary>
    public string? SourceName { get; init; }
}

/// <summary>A module a configured repo ships that this instance does NOT carry, as the last
/// <see cref="ModuleDiscovery"/> scan recorded it. Present only where such a record exists.</summary>
public sealed record ModuleGap
{
    /// <summary>The module id the repo ships it under.</summary>
    public string ModuleId { get; init; } = "";

    /// <summary>Its display name, where the scan recorded one.</summary>
    public string? Name { get; init; }

    /// <summary>The repo that ships it.</summary>
    public string? RepositoryUrl { get; init; }

    /// <summary>Why it is not here — discovered-but-not-synced, occupied, refused, failed, orphaned.</summary>
    public ModuleDiscoveryStatus Status { get; init; }

    /// <summary>The scan's speaking reason.</summary>
    public string? Detail { get; init; }
}

/// <summary>Which of the two recording shapes carry a module.</summary>
public enum ModuleOrigin
{
    /// <summary>Neither shape — never produced by the reader; the default guards a hand-built record.</summary>
    None,

    /// <summary>A <c>{Space}/_GitSync</c> entry: repo import. The production majority.</summary>
    GitSync,

    /// <summary>A <c>Plugins/{id}</c> install record written by the package installer.</summary>
    PackageInstall,

    /// <summary>Both — the module was installed AND is kept in sync from its repo.</summary>
    Both,
}

/// <summary>Which record a <see cref="ModuleCoordinate.ProvenanceRef"/> came from.</summary>
public enum ProvenanceKind
{
    /// <summary>Nothing pins this module: neither shape recorded a ref.</summary>
    None,

    /// <summary>A branch name off the sync entry, with no sync ever recorded. It MOVES.</summary>
    Branch,

    /// <summary>The sync entry's <c>lastSyncCommitSha</c> — the commit the Space was imported from.</summary>
    SyncedCommit,

    /// <summary>The install record's <c>installedFromRef</c> — a commit or a branch.</summary>
    InstalledRef,

    /// <summary>The install record's <c>moduleVersion</c> — a hash over the module's own files.</summary>
    ModuleVersion,
}

/// <summary>Whether a ref identifies one immutable tree, or something that moves under it.</summary>
public enum RefFidelity
{
    /// <summary>No ref at all — the module cannot be re-assembled from what is recorded.</summary>
    Unrecorded,

    /// <summary>A branch or tag: two runs of the same combo can resolve it to different content.</summary>
    Moving,

    /// <summary>A commit sha or a content hash: two runs resolve it identically.</summary>
    Exact,
}
