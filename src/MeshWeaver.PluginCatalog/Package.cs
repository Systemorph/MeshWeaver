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
    /// For <see cref="PackageKind.Code"/> packages only: the NodeType configuration lambda source
    /// (e.g. <c>"config =&gt; config.WithContentType&lt;Widget&gt;().AddLayout(...)"</c>). The installer
    /// synthesizes a <c>NodeType</c> node with this configuration and imports the package's
    /// <c>Source/*.cs</c> files as its Code nodes; the mesh then compiles it live (Roslyn) — no
    /// rebuild, no NuGet.
    /// </summary>
    public string? NodeTypeConfiguration { get; init; }

    /// <summary>Ids of other packages this one depends on. Advisory for now.</summary>
    public ImmutableList<string> Requires { get; init; } = [];

    // ── storefront metadata (read off the root node when listing; all optional) ──

    /// <summary>The store's browse-by-category key (the root node's <c>category</c>).</summary>
    public string? Category { get; init; }

    /// <summary>The root node's icon — an inline <c>&lt;svg&gt;</c>, an emoji, or an image URL.</summary>
    public string? Icon { get; init; }

    /// <summary>The purchase price (the root content's <c>price</c>). Null = not purchasable.</summary>
    public decimal? Price { get; init; }

    /// <summary>ISO currency code of <see cref="Price"/>.</summary>
    public string? Currency { get; init; }

    /// <summary>The store-card picture URL (the root content's <c>poster</c>).</summary>
    public string? Poster { get; init; }

    // ── install-record metadata (null on catalog entries; set when written to the registry) ──

    /// <summary>The git ref (commit/branch) this package was installed from. Null until installed.</summary>
    public string? InstalledFromRef { get; init; }

    /// <summary>When the package was installed (UTC). Null until installed.</summary>
    public DateTimeOffset? InstalledAtUtc { get; init; }

    /// <summary>Number of content nodes upserted on the last install. Null until installed.</summary>
    public int? InstalledNodeCount { get; init; }

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

/// <summary>A single file of a package folder read from the source at a git ref.</summary>
/// <param name="RelativePath">Repo-relative path, e.g. <c>"data-analyst-agent/DataAnalyst.md"</c>.</param>
/// <param name="Content">UTF-8 file text.</param>
public sealed record PackageFile(string RelativePath, string Content);

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
