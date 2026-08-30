using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// An <see cref="IPackageSource"/> for NODE-NATIVE plugin repos — the shape the
/// <c>MeshWeaver.Plugins</c> repo ships (node-per-file, "the node is the manifest"). Each
/// <c>&lt;Plugin&gt;/</c> folder is one plugin — a mesh partition: <c>&lt;Plugin&gt;/index.json</c>
/// is its root (a <c>Space</c> or a <c>Store/Plugin</c>) (the partition root lives INSIDE the folder,
/// the same <c>NodeFileMapper</c> mapping GitSync uses), and the rest of the folder holds the
/// <c>NodeType</c> nodes, their <c>Source/*.cs</c> and docs — every file already a MeshNode at its
/// CANONICAL path (no per-partition rebase). Reuses GitSync's fetch so a deployed portal reads the
/// repo over HTTP.
///
/// <para>A plugin's <see cref="PackageManifest.Version"/> is the repo commit sha, so a new commit
/// surfaces as an available update; the installer then writes only the nodes that actually changed.</para>
/// </summary>
public sealed class NodeRepoPackageSource : IPackageSource
{
    /// <summary>
    /// The root node types that make a top-level folder an installable package: the classic
    /// <c>Space</c> root, the Store's <c>Store/Plugin</c> root (plugins + courses retyped for the
    /// storefront), and the Store package's own <c>Store/Catalog</c> root. All accepted during the
    /// transition — a repo may carry a mix.
    /// </summary>
    private static readonly ImmutableHashSet<string> RootNodeTypes =
        ImmutableHashSet.Create(StringComparer.Ordinal, "Space", "Store/Plugin", "Store/Catalog");

    private readonly Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch;
    private readonly string repoUrl;
    private readonly Func<IObservable<string>> tokenProvider;
    private readonly ILogger? logger;

    /// <summary>
    /// Creates a node-repo source that resolves its access token FRESH before each fetch via
    /// <paramref name="tokenProvider"/> — so the registry hands in
    /// <see cref="GitHubAppTokenService.GetInstallationToken"/>, whose short-lived (1h) installation
    /// token is re-minted transparently and never captured stale. The provider may emit an empty
    /// string for anonymous (public-repo) access.
    /// </summary>
    public NodeRepoPackageSource(
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch,
        string repoUrl,
        Func<IObservable<string>> tokenProvider,
        ILogger? logger = null,
        string? defaultLicense = null)
    {
        this.fetch = fetch;
        this.repoUrl = repoUrl;
        this.tokenProvider = tokenProvider;
        this.logger = logger;
        // The licence this SOURCE's repo is published under, from its own LICENSE file. Applied
        // only where a package declares none — it records an existing grant, never invents one.
        this.defaultLicense = defaultLicense;
    }

    private readonly string? defaultLicense;

    /// <summary>
    /// Creates a node-repo source with a FIXED token (default empty = anonymous). Convenience for
    /// tests and public repos; the registry uses the token-provider overload so the App installation
    /// token stays fresh.
    /// </summary>
    public NodeRepoPackageSource(
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch,
        string repoUrl,
        string token = "",
        ILogger? logger = null,
        string? defaultLicense = null)
        : this(fetch, repoUrl, () => Observable.Return(token), logger, defaultLicense)
    {
    }

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
        tokenProvider().SelectMany(token => fetch(repoUrl, gitRef, null, token))
            .Select(snapshot =>
            {
                // The CI-maintained manifest sidecar (`<Plugin>/manifest.lock`): its moduleVersion
                // rides on the catalog entry so a consumer can decide "nothing to sync" without
                // fetching a single package file. Tolerant: a missing/broken sidecar just leaves
                // ModuleVersion null (legacy commit-sha comparison applies).
                // Keep the WHOLE parsed manifest, not just its version. ModuleVersion answers
                // "did this module change at all"; the per-file hash map answers "what changed",
                // which is what a subscriber to the repo's BuildCompletion node diffs against the
                // install record's InstalledFiles to report an actual file list. Discarding Files
                // here would force a second fetch of the same sidecar later.
                var moduleManifests = new Dictionary<string, ModuleManifest>(StringComparer.Ordinal);
                foreach (var file in snapshot.Files)
                {
                    var slash = file.Path.IndexOf('/');
                    if (slash <= 0
                        || file.Path.IndexOf('/', slash + 1) >= 0
                        || !file.Path.AsSpan(slash + 1).Equals(ModuleManifest.FileName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var parsed = ModuleManifest.TryParse(file.Content, logger);
                    if (parsed is not null)
                        moduleManifests[file.Path[..slash]] = parsed;
                }

                var manifests = new List<PackageManifest>();
                foreach (var file in snapshot.Files)
                {
                    // A plugin root is `<Plugin>/index.json` — the partition root INSIDE its folder
                    // (the folder is the unit of import; NodeFileMapper maps root ↔ index.json) —
                    // whose node is one of the accepted root types (Space / Store/Plugin). Checked
                    // allocation-free: exactly one '/' and an `index.json` tail; only a match
                    // slices out the id.
                    var slash = file.Path.IndexOf('/');
                    if (slash <= 0
                        || file.Path.IndexOf('/', slash + 1) >= 0
                        || !file.Path.AsSpan(slash + 1).Equals("index.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var peeked = Peek(file.Content, file.Path);
                    if (peeked.NodeType is null || !RootNodeTypes.Contains(peeked.NodeType))
                        continue;
                    var id = file.Path[..slash];
                    manifests.Add(new PackageManifest
                    {
                        Id = id,
                        Name = peeked.Name ?? id,
                        Description = peeked.Description,
                        Kind = PackageKind.NodeRepo,
                        TargetPartition = id,
                        SourceFolder = id,
                        // 🚨 The module's OWN released SemVer when it has one, and only then the
                        // whole-repo commit sha. A sha is unorderable and repo-wide: every module in
                        // the repo "changed version" whenever any one of them did, and no dependent
                        // could express a constraint against it. The manifest's version is per module
                        // and ordered, so it is what a pin resolves to; the sha stays as the fallback
                        // for a module whose manifest predates versioning.
                        Version = moduleManifests.TryGetValue(id, out var mm) && mm.Version is { } semver
                            ? semver
                            : snapshot.CommitSha,
                        ModuleVersion = mm?.ModuleVersion,
                        // The candidate file set at this ref. Diffed against the install record's
                        // InstalledFiles to produce the added/modified/removed list; null when the
                        // module ships no manifest.lock (legacy commit-sha comparison applies).
                        ManifestFiles = mm?.Files,
                        Requires = peeked.Requires,
                        License = peeked.License ?? defaultLicense,
                        Category = peeked.Category,
                        Icon = peeked.Icon,
                        Price = peeked.Price,
                        Currency = peeked.Currency,
                        // The plan the package belongs to — what a plan-scoped grant licenses it by.
                        Tier = peeked.Tier,
                        // Contact-sales: commercial without a price (PackageEntitlement.IsCommercial).
                        ContactEmail = peeked.ContactEmail,
                        Poster = peeked.Poster,
                        // The package's own declaration that it belongs to the platform's default
                        // install — read off the ROOT's content, where the plugins repo authors it.
                        PreInstalled = peeked.PreInstalled,
                        // The declared public surface — what the installer's access step scopes a
                        // free package's public read to.
                        PublicSegments = peeked.PublicSegments,
                        // The compiled-module declaration (#1664) — what routes this package
                        // through the module bundle funnel on top of its content install — and
                        // its declared platform floor, the module lane's landing gate.
                        Module = peeked.Module,
                        MinMeshVersion = peeked.MinMeshVersion,
                        // What this package needs its ENVIRONMENT to supply. Read here for the same
                        // dead-metadata reason as everything above: the install funnel's parameter
                        // gate keys on the manifest, so a field the listing drops is a gate that
                        // never fires.
                        Parameters = peeked.Parameters,
                    });
                }
                return (IReadOnlyList<PackageManifest>)manifests
                    .OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
            });

    /// <inheritdoc />
    public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
        tokenProvider().SelectMany(token => fetch(repoUrl, gitRef, null, token))
            .Select(snapshot =>
            {
                // The whole plugin — root included — lives under `<Plugin>/`.
                var folderPrefix = package.Id + "/";
                return (IReadOnlyList<PackageFile>)snapshot.Files
                    // 🚨 Carry `Binary` too. A non-UTF-8 blob (a course video/poster under
                    // `<Plugin>/content/**`) has an EMPTY `Content` by design — RepoFileCodec puts
                    // its bytes on `RepoFile.Binary` so a UTF-8 round-trip cannot corrupt them.
                    // Projecting only `Content` here is what served every binary as `content = ""`
                    // and left merged course videos 404ing until someone uploaded them by hand
                    // (issue #848).
                    .Where(f => f.Path.StartsWith(folderPrefix, StringComparison.Ordinal))
                    .Select(f => new PackageFile(f.Path, f.Content, f.Binary))
                    .ToList();
            });

    private readonly record struct PeekedRoot(
        string? NodeType, string? Name, string? Description,
        string? Category, string? Icon, decimal? Price, string? Currency, string? Poster,
        bool PreInstalled, ImmutableList<string> Requires, ImmutableList<string> PublicSegments,
        string? License, string? ContactEmail, string? Module, string? MinMeshVersion,
        ImmutableList<PackageParameter> Parameters, string? Tier);

    // Reads the node's type/name/description — plus the storefront card fields (category/icon on
    // the node, price/currency/poster inside the content) — straight from the JSON: no MeshNode
    // deserialization, so the source needs no hub serializer options and unregistered content
    // types (PluginManifest / PluginContent) don't matter for listing.
    private PeekedRoot Peek(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            decimal? price = null;
            string? currency = null;
            string? poster = null;
            string? contactEmail = null;
            var preInstalled = false;
            var requires = ImmutableList<string>.Empty;
            string? license = null;
            var publicSegments = ImmutableList<string>.Empty;
            string? module = null;
            string? minMeshVersion = null;
            var parameters = ImmutableList<PackageParameter>.Empty;
            string? tier = null;
            if (r.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
            {
                // The package's PLAN ("tier": free / personal / pro / dedicated / enterprise) — what a
                // plan-scoped grant entry licenses it by. Same dead-metadata rationale as every field
                // below: the registry's entitlement decision keys on the manifest, so a tier the
                // listing drops is a plan that licenses nothing.
                if (content.TryGetProperty("tier", out var t) && t.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(t.GetString()))
                    tier = PlanTierRanks.Canonical(t.GetString());
                if (content.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number
                    && p.TryGetDecimal(out var parsed))
                    price = parsed;
                if (content.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String)
                    currency = c.GetString();
                if (content.TryGetProperty("poster", out var po) && po.ValueKind == JsonValueKind.String)
                    poster = po.GetString();
                // The sales contact ("contactEmail"). A contact-sales package normally names NO
                // price, so leaving this unread is what made it arrive as free — installable with
                // no admin and published by the access step. See PackageManifest.ContactEmail.
                if (content.TryGetProperty("contactEmail", out var ce) && ce.ValueKind == JsonValueKind.String)
                    contactEmail = ce.GetString();
                // Only a literal `true` opts in — anything else (absent, false, a string) leaves the
                // package out of the default install. A malformed value must never silently install
                // a package on every instance.
                if (content.TryGetProperty("preInstalled", out var pre))
                    preInstalled = pre.ValueKind == JsonValueKind.True;
                // 🚨 The plugin's declared dependencies ("Store@^1.0.0"). Read here because an
                // UNATTENDED install has to order by them: installing a package before the one it
                // depends on fails outright ("NodeType(s) not registered: Training/Tour" — Chess
                // installed before Training on the first live run). A human clicking Install picks
                // the order themselves, which is why this stayed unread for so long.
                // The package's own declared licence (SPDX id or expression). Absent leaves it
                // null — the source's DefaultLicense fills it, never a platform-wide guess.
                if (content.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.String)
                    license = lic.GetString();
                if (content.TryGetProperty("requires", out var req) && req.ValueKind == JsonValueKind.Array)
                    requires = req.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToImmutableList();
                // The plugin's declared public surface ("publicSegments"). Read here because the
                // INSTALLER establishes the declared access at install time — a free package with
                // declared segments gets those public and the rest gated, and leaving the field
                // unread made it dead metadata (the same defect class `preInstalled` was, #920).
                if (content.TryGetProperty("publicSegments", out var pub) && pub.ValueKind == JsonValueKind.Array)
                    publicSegments = pub.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToImmutableList();
                // The package's compiled-module declaration ("module": the entry-assembly name,
                // #1664). Read here because the INSTALL funnel and the registry's bundle index both
                // key on the manifest — leaving it unread would make it dead metadata, the defect
                // class `preInstalled`/`publicSegments`/`contactEmail` each had.
                if (content.TryGetProperty("module", out var mod) && mod.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(mod.GetString()))
                    module = mod.GetString();
                // The declared platform FLOOR ("minMeshVersion" — the field authors already
                // write): the module lane's landing gate. Same dead-metadata rationale as above.
                if (content.TryGetProperty("minMeshVersion", out var floor)
                    && floor.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(floor.GetString()))
                    minMeshVersion = floor.GetString();
                // What the package needs its ENVIRONMENT to supply ("parameters": [{name, kind,
                // service, description, optional}]). Deserialized with the same web-style options
                // the package.json sources use, so one authored shape reads identically in both
                // repo formats. A malformed entry is dropped rather than taking the whole listing
                // down — but a DROPPED required parameter would silently disarm the install gate,
                // so the drop is logged.
                if (content.TryGetProperty("parameters", out var pars)
                    && pars.ValueKind == JsonValueKind.Array)
                    parameters = pars.EnumerateArray()
                        .Select(e => Parameter(e, path))
                        .Where(p => p is not null)
                        .Select(p => p!)
                        .ToImmutableList();
            }
            return new PeekedRoot(
                r.TryGetProperty("nodeType", out var nt) ? nt.GetString() : null,
                r.TryGetProperty("name", out var n) ? n.GetString() : null,
                r.TryGetProperty("description", out var d) ? d.GetString() : null,
                r.TryGetProperty("category", out var cat) ? cat.GetString() : null,
                r.TryGetProperty("icon", out var ic) ? ic.GetString() : null,
                price, currency, poster, preInstalled, requires, publicSegments, license,
                contactEmail, module, minMeshVersion, parameters, tier);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Node-repo catalog: {Path} is not valid JSON; skipped.", path);
            return default;
        }
    }

    /// <summary>Web-style options, so one authored <c>parameters</c> shape reads identically here and
    /// in the <c>package.json</c> sources (which deserialize the whole manifest with the same).</summary>
    private static readonly JsonSerializerOptions ParameterJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One entry of the root's <c>parameters</c> array. A malformed entry — or one with no name — is
    /// DROPPED and NAMED: silently dropping a required parameter would disarm the install gate that
    /// exists to refuse a half-configured install, which is worse than the malformed manifest.
    /// </summary>
    private PackageParameter? Parameter(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            logger?.LogWarning(
                "Node-repo catalog: {Path} declares a non-object entry in `parameters`; skipped.", path);
            return null;
        }
        try
        {
            var parameter = element.Deserialize<PackageParameter>(ParameterJson);
            if (parameter is null || string.IsNullOrWhiteSpace(parameter.Name))
            {
                logger?.LogWarning(
                    "Node-repo catalog: {Path} declares a parameter with no name; skipped.", path);
                return null;
            }
            return parameter;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex,
                "Node-repo catalog: {Path} declares an unreadable parameter; skipped.", path);
            return null;
        }
    }
}
