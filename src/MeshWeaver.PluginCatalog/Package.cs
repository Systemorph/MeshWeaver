using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json.Serialization;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What an installable package delivers into the mesh.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PackageKind>))]
public enum PackageKind
{
    /// <summary>Authored mesh content (agents, skills, model providers, docs, decks,
    /// course material) — installed by importing the folder's nodes into a partition.</summary>
    Content,

    /// <summary>A capability shipped as SOURCE: the manifest's <c>nodeTypeConfiguration</c> plus the
    /// package's <c>Source/*.cs</c> become a NodeType the mesh compiles live (Roslyn) on install via
    /// its existing compile/release flow — no rebuild, no NuGet.</summary>
    Code,

    /// <summary>A whole node-native plugin repo (node-per-file): a Space root carrying a
    /// <c>PluginManifest</c>, its <c>NodeType</c> nodes and their <c>Source/*.cs</c>, docs and data —
    /// each file is already a MeshNode at its CANONICAL path (no per-partition rebase). Installed by
    /// importing the nodes verbatim and compiling every NodeType live. This is the shape the
    /// <c>MeshWeaver.Plugins</c> repo ships (the node IS the manifest).</summary>
    NodeRepo,
}

/// <summary>
/// The manifest describing one installable package. In the plugins repo each installable folder
/// carries a <c>package.json</c> that deserializes to this record; it is also the content of the
/// <c>Package</c> node written to the installed-packages registry so the catalog can show
/// installed / update-available status. Kept deliberately small.
/// </summary>
public record PackageManifest
{
    /// <summary>Stable package id (e.g. <c>"data-analyst-agent"</c>). Also the installed-record id.</summary>
    public string Id { get; init; } = "";

    /// <summary>Human-readable display name.</summary>
    public string? Name { get; init; }

    /// <summary>One-line description shown in the catalog.</summary>
    public string? Description { get; init; }

    /// <summary>What the package delivers (content today; code later).</summary>
    public PackageKind Kind { get; init; } = PackageKind.Content;

    /// <summary>The mesh partition the package installs into (e.g. <c>"Agent"</c>, <c>"Skill"</c>).</summary>
    public string? TargetPartition { get; init; }

    /// <summary>Package version string (compared to the installed record for update-available status).</summary>
    public string? Version { get; init; }

    /// <summary>
    /// The module's content version from its CI-maintained <c>manifest.lock</c>
    /// (<see cref="ModuleManifest.ModuleVersion"/>) — a hash of the module's own files, unlike
    /// <see cref="Version"/> which is the whole-repo commit sha. When both the catalog entry and the
    /// installed record carry it, equality means "nothing to sync": the card shows Installed and an
    /// update request skips without fetching a single file. Null for packages without a manifest
    /// (legacy behavior applies).
    /// </summary>
    public string? ModuleVersion { get; init; }

    /// <summary>
    /// The module's RELEASED SemVer from its <c>manifest.lock</c>
    /// (<see cref="ModuleManifest.Version"/>) — the number published as the git tag
    /// <c>&lt;Module&gt;/vX.Y.Z</c> and the one dependents pin against.
    ///
    /// <para>🚨 Distinct from all three neighbours, and none of them substitutes for it:
    /// <see cref="Version"/> is the whole-repo commit sha, <see cref="ModuleVersion"/> is a content
    /// HASH (exact but unordered — it cannot express "newer"), and the plugin node's own
    /// <c>PluginContent.version</c> carries only the AUTHORED <c>MAJOR.MINOR</c>. The PATCH is
    /// derived by <c>gen-manifests.py</c> from the content hash, so ThreeBody reads <c>1.3</c> on
    /// its node while its lock and tag say <c>1.3.2</c>.</para>
    ///
    /// <para>Persisted because the portal otherwise has no way to name the version a module was
    /// actually released at: the lock is a repo artifact that GitSync does not carry into the mesh.
    /// A package feed that invented its own number instead would fork the version namespace away
    /// from the tags — the exact drift <c>tag-modules.py</c> exists to prevent ("one version, one
    /// tree — forever").</para>
    ///
    /// <para>Null for a package whose manifest predates versioning.</para>
    /// </summary>
    public string? ReleasedVersion { get; init; }

    /// <summary>The package's folder within the source repo. Set by the source while listing;
    /// not authored in <c>package.json</c>.</summary>
    public string? SourceFolder { get; init; }

    /// <summary>
    /// The registry SOURCE this package came from (<c>PluginCatalog:Sources:N:Name</c> — e.g.
    /// <c>Plugins</c>, <c>Education</c>). Stamped by the registry as it merges its sources; never
    /// authored in a repo. It is what lets a consumer scope an action to a source it trusts —
    /// specifically <c>PluginCatalog:InstallByDefault</c>, which must be able to say "install the
    /// platform repo" WITHOUT sweeping in paid course content the same instance may also be
    /// granted. Null when the registry predates this field: a source-scoped default then matches
    /// nothing, which fails closed (installs nothing) rather than installing the wrong thing.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// The licence this package is offered under, as an SPDX id or expression
    /// (<c>Apache-2.0</c>, <c>Apache-2.0 OR MIT</c>). Authored on the plugin root's
    /// <c>content.license</c>; when absent it is filled from the SOURCE's declared default (a
    /// repo's own LICENSE, recorded once in <c>PluginCatalog:Sources:N:DefaultLicense</c>).
    ///
    /// <para>🚨 Null means UNSPECIFIED, and must stay null rather than defaulting to anything.
    /// A fallback records a grant the copyright holder already made; inventing one for a
    /// third-party repo would assert a licence its author never gave. Surfacing "unspecified" is
    /// the honest answer, and it is what lets a UI ask before installing.</para>
    /// </summary>
    public string? License { get; init; }

    /// <summary>
    /// For <see cref="PackageKind.Code"/> packages only: the NodeType configuration lambda source
    /// (e.g. <c>"config =&gt; config.WithContentType&lt;Widget&gt;().AddLayout(...)"</c>). The installer
    /// synthesizes a <c>NodeType</c> node with this configuration and imports the package's
    /// <c>Source/*.cs</c> files as its Code nodes; the mesh then compiles it live (Roslyn) — no
    /// rebuild, no NuGet.
    /// </summary>
    public string? NodeTypeConfiguration { get; init; }

    /// <summary>Ids of other packages this one depends on. Advisory for now.</summary>
    public ImmutableList<string> Requires { get; init; } = [];

    /// <summary>
    /// The parameters this package needs its ENVIRONMENT to supply — a connection string, another
    /// service's endpoint, a provisioned value (the package root's <c>parameters</c> array).
    ///
    /// <para>🚨 A declared parameter the environment does not supply REFUSES the install, loudly,
    /// naming the exact env var to provision (<see cref="PackageParameters.Require"/>, on the one
    /// install funnel). Never a half-configured install and never a silent skip: content that
    /// installs without its connection string fails at first use with nothing pointing back at the
    /// missing key. <c>Optional = true</c> opts a parameter out of the gate.</para>
    ///
    /// <para>Empty (the default) = the package needs nothing beyond the platform, which is every
    /// package before this field existed; the empty default round-trips loss-free under
    /// default-suppressing serialization.</para>
    /// </summary>
    public ImmutableList<PackageParameter> Parameters { get; init; } = [];

    /// <summary>
    /// The compiled MODULE this package delivers (#1664): the module's entry-assembly name WITHOUT
    /// extension (e.g. <c>MeshWeaver.Social</c>) — the same identity <c>Modules:Assemblies</c>
    /// entries and <c>modules/&lt;name&gt;/</c> folders use. Null (the default) = a pure content /
    /// code package, which is every package before this field existed.
    ///
    /// <para>Non-null routes the package through the module funnel ON TOP of its normal content
    /// install: after the content lands, the consumer fetches the package's bundle from the
    /// registry's <c>/api/plugins/bundles</c>, verifies the framework MVID
    /// (<c>PrebuiltAssemblySeeder.DeclineReason</c> — the ONE identity), and lands the module via
    /// <c>ModuleLandingService</c> (restart-as-activation). The registry side serves the module's
    /// bytes for any installed record carrying this field. A MIXED package — content nodes plus a
    /// compiled module in one Store product (the MeshWeaver.SocialMedia shape) — is exactly this
    /// field on an ordinary node-repo package.</para>
    ///
    /// <para>Authored on the plugin root's <c>content.module</c> (node-repo format) or a
    /// <c>package.json</c>; read while listing (<see cref="NodeRepoPackageSource"/>) and carried
    /// onto the install record by the ordinary record stamp, so the registry's bundle index can
    /// offer the module without re-reading the repo.</para>
    /// </summary>
    public string? Module { get; init; }

    /// <summary>
    /// The package's declared platform FLOOR (<c>content.minMeshVersion</c> — the field plugin
    /// authors already write): the minimum MeshWeaver version its compiled module requires. For a
    /// module-declaring package this is THE landing gate
    /// (<see cref="ModulePlatformFloor.DeclineReason(string?)"/>) — deliberately a semver floor,
    /// never MVID equality, because a module is a plain assembly binding by simple name whose
    /// contract is API compatibility. Null = no constraint (most modules need none). Carried onto
    /// the install record and surfaced on the registry's bundle index so a consumer skips an
    /// uninstallable bundle without downloading it.
    /// </summary>
    public string? MinMeshVersion { get; init; }

    /// <summary>
    /// The package is part of the platform's DEFAULT INSTALL: every instance that can see it in a
    /// catalog installs it automatically at startup (<see cref="InstanceAutoRegistrationService"/>),
    /// with public read established by the installer
    /// (<c>{TargetPartition}/_Policy · PublicRead = true</c>) so its content is reachable by every
    /// user — signed in or not — without a per-user or per-instance install step.
    ///
    /// <para>Authored on the package root's own content (<c>"preInstalled": true</c> inside the
    /// node-repo <c>index.json</c>'s <c>content</c>, or on a <c>package.json</c> manifest) and read
    /// off it while listing. It is the manifest's ONE statement of "this ships with the platform";
    /// nothing else needs configuring, and the flag is honoured identically on a registry instance
    /// (its own git sources) and on a consumer instance (the registries it pulls from).</para>
    ///
    /// <para>Default <c>false</c> — the CLR default — so it round-trips loss-free under
    /// default-suppressing serialization (the declared-<c>true</c> bool trap, see
    /// <see cref="AutoUpdate"/>).</para>
    /// </summary>
    public bool PreInstalled { get; init; }

    // ── storefront metadata (read off the root node when listing; all optional) ──

    /// <summary>The store's browse-by-category key (the root node's <c>category</c>).</summary>
    public string? Category { get; init; }

    /// <summary>The root node's icon — an inline <c>&lt;svg&gt;</c>, an emoji, or an image URL.</summary>
    public string? Icon { get; init; }

    /// <summary>The purchase price (the root content's <c>price</c>). Null = not purchasable.</summary>
    public decimal? Price { get; init; }

    /// <summary>ISO currency code of <see cref="Price"/>.</summary>
    public string? Currency { get; init; }

    /// <summary>
    /// The subscription PLAN this package belongs to (the root content's <c>tier</c> — <c>free</c>,
    /// <c>personal</c>, <c>pro</c>, <c>dedicated</c>, <c>enterprise</c>), lower-cased; null when the
    /// package declares none, which the registry reads as platform BASELINE (rank 0 — covered by
    /// every plan).
    ///
    /// <para>🚨 Read here because the registry's ENTITLEMENT decision keys on it: a plan-scoped
    /// grant entry (<c>Plugins/*@personal</c>) licenses the packages of a source by their tier,
    /// and leaving the field unread would make every such entry license nothing — the same
    /// dead-metadata class <c>preInstalled</c>, <c>contactEmail</c> and <c>module</c> each were.
    /// Carried onto the install record by the ordinary stamp, so the bundle index can decide
    /// without re-reading the repo.</para>
    /// </summary>
    public string? Tier { get; init; }

    /// <summary>
    /// The sales contact (the root content's <c>contactEmail</c>). Set = the package is sold
    /// CONTACT-SALES rather than self-service: the Store's cover offers "Contact sales" instead of a
    /// buy button, and the content is gated exactly as a priced package's is.
    ///
    /// <para>🚨 Read here because the ENTITLEMENT and ACCESS decisions key on the manifest, not on
    /// the Store's own view of the root: a contact-sales package that named no price used to arrive
    /// as FREE, so <see cref="PackageEntitlement.Authorize"/> waved it through with no admin and
    /// <see cref="PackageInstaller.EnsureDeclaredAccess"/> published the whole partition
    /// (<c>_Policy · PublicRead = true</c>) until the Store's <c>PluginGate</c> darkened it again.
    /// The package said "talk to us before you use this" and the install path could not hear it —
    /// the same dead-metadata defect <c>preInstalled</c> and <c>publicSegments</c> each had (#920).
    /// <see cref="PackageEntitlement.IsCommercial"/> now counts it.</para>
    /// </summary>
    public string? ContactEmail { get; init; }

    /// <summary>The store-card picture URL (the root content's <c>poster</c>).</summary>
    public string? Poster { get; init; }

    /// <summary>
    /// The partition's declared PUBLIC child segments (the root content's <c>publicSegments</c>).
    /// For a FREE package (<see cref="Price"/> 0 or absent) that declares any, the installer scopes
    /// the public read to exactly these segments: root Public+Anonymous Viewer grants (the cover and
    /// the declared segments become readable by everyone) plus Public+Anonymous Viewer DENIES on
    /// every other child segment — the same shape the Store's <c>CatalogGate</c>/<c>PluginGate</c>
    /// seed. Empty (the default) on a free package means the WHOLE partition is public
    /// (<c>_Policy · PublicRead = true</c>); on a priced package the field is advisory to the
    /// entitlement machinery and the installer writes nothing. Read off the root node while listing
    /// (<see cref="NodeRepoPackageSource"/>) or straight from a <c>package.json</c>; empty default
    /// round-trips loss-free under default-suppressing serialization.
    /// </summary>
    public ImmutableList<string> PublicSegments { get; init; } = [];

    // ── install-record metadata (null on catalog entries; set when written to the registry) ──

    /// <summary>The git ref (commit/branch) this package was installed from. Null until installed.</summary>
    public string? InstalledFromRef { get; init; }

    /// <summary>When the package was installed (UTC). Null until installed.</summary>
    public DateTimeOffset? InstalledAtUtc { get; init; }

    /// <summary>Number of content nodes upserted on the last install. Null until installed.</summary>
    public int? InstalledNodeCount { get; init; }

    /// <summary>
    /// The principal that AUTHORIZED this install — the global admin who clicked Install on a
    /// commercial package (<see cref="PackageEntitlement.IsCommercial"/>). Null on a free package
    /// and on anything installed unattended (boot-time provisioning has no principal).
    ///
    /// <para>It exists so an UNATTENDED update can be authorized the same way the install was
    /// (#830): <see cref="PluginUpdateWatcher"/> re-verifies this principal is STILL a global admin
    /// before applying a commercial package's delta, so revoking the admin stops the syncing too. A
    /// re-stamp carries the existing value forward (<c>PackageInstaller.SeedAuthorizedBy</c>) — the
    /// record built on an update starts from the catalog manifest, which never carries it.</para>
    /// </summary>
    public string? AuthorizedBy { get; init; }

    /// <summary>
    /// The module manifest's per-file hash map at install time (<see cref="ModuleManifest.Files"/>)
    /// — the baseline the NEXT update diffs against to touch only what really changed. Null until
    /// installed from a manifest-carrying package (a null baseline falls back to the legacy full
    /// install path).
    /// </summary>
    public ImmutableSortedDictionary<string, string>? InstalledFiles { get; init; }

    /// <summary>
    /// Opt IN to unattended updates for this installed package: when the source repo's CI goes
    /// green and this module's content hash actually moved, install the delta without waiting for
    /// a human to click Update. Unset — the platform default — the record stays on the reminder
    /// path (an "Update available" notification, nothing installed).
    ///
    /// <para><b>Seeded at install time from the deployment's policy</b>
    /// (<see cref="PluginCatalogOptions.AutoUpdateByDefault"/>): a deployment that opts in — ours
    /// do, via the Helm chart — gets every freshly installed package auto-updating with no
    /// per-package step, while the platform default stays explicit-opt-in. Thereafter the record's
    /// own value is the sole runtime authority in BOTH directions: an update re-stamp carries it
    /// forward (see <c>PackageInstaller.SeedAutoUpdate</c> — the re-stamp starts from the
    /// policy-less catalog manifest, so without the carry-forward every update would silently
    /// reset the opt-in), and flipping the deployment default later changes nothing for
    /// already-installed packages.</para>
    ///
    /// <para>Opted-in updates are still fenced three ways: the content-identity gate (an unchanged
    /// module is never touched), the additive install (a node the user ADDED is structurally
    /// invisible to the update), and the per-node
    /// <see cref="MeshWeaver.Mesh.MeshNode.SyncBehavior"/> claim (a node the user MODIFIED and
    /// claimed is skipped by both upsert and prune).</para>
    ///
    /// <para>Default <c>false</c> — the CLR default — so the flag round-trips loss-free under
    /// default-suppressing serialization (a declared-<c>true</c> bool loses its true→false
    /// transition; the trap already diagnosed on this codebase). Only meaningful on an INSTALL
    /// RECORD, and only consulted when the module's <see cref="ModuleVersion"/> has genuinely
    /// moved. See <c>Doc/Architecture/PluginUpdateOnGreenBuild</c>.</para>
    /// </summary>
    public bool AutoUpdate { get; init; }

    /// <summary>
    /// The candidate <see cref="ModuleVersion"/> this installation has ALREADY told the user
    /// about — the reminder path's own silence gate, and the second half of
    /// Systemorph/MeshWeaver#3213.
    ///
    /// <para><b>Why <see cref="ModuleVersion"/> alone cannot do this job.</b> The content-identity
    /// gate in <c>PackageUpdateReconciler.Decide</c> asks <em>is the candidate INSTALLED?</em>.
    /// On the <see cref="AutoUpdate"/> path that is self-silencing: the apply advances
    /// <see cref="ModuleVersion"/>, so the next poll compares equal. On the reminder path nothing
    /// is installed — by design, the user has not acted — so the same comparison is
    /// <b>unsatisfiable</b> and evaluated false on every subsequent poll, forever. The notify path
    /// needs the other question, <em>have I already told them about this candidate?</em>, and this
    /// is where its answer lives.</para>
    ///
    /// <para>It is deliberately a fact on the DURABLE record rather than an in-memory de-dup
    /// cache: a cache loses its memory on every pod start and re-notifies, which is the symptom
    /// itself, and process-wide static state is forbidden here anyway.</para>
    ///
    /// <para>Written by the reconciler AFTER the reminder is raised (so a failed write costs a
    /// harmless repeat, which the notification's deterministic identity then absorbs, rather than
    /// a lost reminder). Null on every record that has never been reminded, which is every record
    /// written before this field existed — so the first poll after an upgrade reminds once and
    /// then goes quiet. A record whose <see cref="ModuleVersion"/> catches up (the user clicked
    /// Update) is silenced by the content-identity gate before this one is ever consulted, so a
    /// re-stamp that drops this field cannot resurrect the storm.</para>
    /// </summary>
    public string? NotifiedModuleVersion { get; init; }

    /// <summary>
    /// The module manifest's per-file hash map as it stands AT THE CATALOG'S REF
    /// (<see cref="ModuleManifest.Files"/>) — the candidate side of the diff, where
    /// <see cref="InstalledFiles"/> is the installed side. Set by the source while listing; never
    /// authored and never written to the install record (the record stores the map it installed, as
    /// <see cref="InstalledFiles"/>).
    ///
    /// <para>Diffing the two yields the actual added/modified/removed file list that a
    /// build-completion subscriber reports, so nothing has to re-fetch the sidecar it already read.
    /// Null when the module ships no <c>manifest.lock</c> — the legacy commit-sha comparison applies
    /// then, and no file-level diff is available.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableSortedDictionary<string, string>? ManifestFiles { get; init; }
}

/// <summary>
/// A single file of a package folder read from the source at a git ref. A TEXT file carries its
/// UTF-8 text in <paramref name="Content"/> (and <see cref="Binary"/> is null); a BINARY file — a
/// course video/poster committed under <c>{package}/content/**</c>, a font, any non-UTF-8 blob —
/// carries its raw bytes in <see cref="Binary"/> and leaves <paramref name="Content"/> EMPTY,
/// because round-tripping arbitrary bytes through a UTF-8 string corrupts them. Read
/// <see cref="Bytes"/> for the content regardless of kind.
///
/// <para>This mirrors <see cref="MeshWeaver.GitSync.RepoFile"/> exactly, and that alignment is what
/// makes "merging publishes the package COMPLETELY" true (issue #848). The git transports already
/// classify binaries correctly and put their bytes on <c>RepoFile.Binary</c> — but the package
/// sources DROPPED that field, so every binary reached <c>POST /api/plugins/files</c> as
/// <c>content = ""</c> (the measured "0 chars") and the installer had nothing to write.</para>
///
/// <para><b>Wire form.</b> <c>System.Text.Json</c> encodes a <c>byte[]</c> as base64 in both
/// directions, so the registry payload carries binaries with no custom converter — at ~4/3 the raw
/// size. An OLD registry simply omits the field, leaving <see cref="Binary"/> null, and the consumer
/// then behaves exactly as before: producer and consumer roll independently.</para>
/// </summary>
/// <param name="RelativePath">Repo-relative path, e.g. <c>"data-analyst-agent/DataAnalyst.md"</c>.</param>
/// <param name="Content">The file's UTF-8 text (empty for a binary file — read <see cref="Bytes"/>).</param>
/// <param name="Binary">The file's raw bytes when it is NOT valid UTF-8 text; null for a text file.</param>
public sealed record PackageFile(string RelativePath, string Content, byte[]? Binary = null)
{
    /// <summary>True when this file holds raw (non-text) bytes that must never pass through the text API.</summary>
    /// <remarks>🚨 <see cref="JsonIgnoreAttribute"/> is load-bearing, not tidiness: this record IS the
    /// <c>/api/plugins/files</c> wire shape, and a serialized computed property would ship a SECOND
    /// full base64 copy of every binary (a 9 MB video twice over) in each response.</remarks>
    [JsonIgnore]
    public bool IsBinary => Binary is not null;

    /// <summary>The file's raw bytes: <see cref="Binary"/> for a binary file, else the UTF-8 encoding of <see cref="Content"/>.</summary>
    /// <remarks>See <see cref="IsBinary"/> — <see cref="JsonIgnoreAttribute"/> keeps the payload from
    /// carrying the bytes twice.</remarks>
    [JsonIgnore]
    public byte[] Bytes => Binary ?? System.Text.Encoding.UTF8.GetBytes(Content);
}

/// <summary>
/// A source of installable packages — a git repo at a chosen ref. Lists the packages (folders with
/// a manifest) and fetches a package folder's files, so <see cref="PackageInstaller"/> can import
/// them. The one MVP implementation reads a LOCAL git repo via the <c>git</c> CLI (no NuGet); a
/// GitHub-fetch implementation slots in behind the same interface later.
/// </summary>
public interface IPackageSource
{
    /// <summary>Lists installable packages at <paramref name="gitRef"/> (each top-level folder that
    /// carries a <c>package.json</c>).</summary>
    IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef);

    /// <summary>Fetches every file of <paramref name="package"/>'s folder at <paramref name="gitRef"/>
    /// (the manifest itself is included; the installer skips it).</summary>
    IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef);

    /// <summary>
    /// Fetches only the given <paramref name="paths"/> (repo-relative) of the package — the
    /// incremental-update fast path driven by a <see cref="ModuleManifest"/> diff. Null paths =
    /// everything. The default implementation filters the full fetch locally; remote sources
    /// (<see cref="RegistryPackageSource"/>) override it to move the filter to the server so
    /// unchanged files never travel. Paths absent from the package simply don't appear in the
    /// result.
    /// </summary>
    IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
        PackageManifest package, string gitRef, IReadOnlyCollection<string>? paths) =>
        paths is null
            ? FetchPackageFiles(package, gitRef)
            : FetchPackageFiles(package, gitRef)
                .Select(files =>
                {
                    var wanted = paths as ISet<string> ?? new HashSet<string>(paths, StringComparer.Ordinal);
                    return (IReadOnlyList<PackageFile>)files
                        .Where(f => wanted.Contains(f.RelativePath))
                        .ToList();
                });
}
