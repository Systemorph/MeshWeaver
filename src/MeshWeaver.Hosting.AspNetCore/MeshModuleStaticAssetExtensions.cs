using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace MeshWeaver.Hosting.AspNetCore;

/// <summary>
/// Serves the static web assets of modules that ship in <c>modules/&lt;Name&gt;/</c> — the host
/// half of the module static-asset lane (issue #1724, design #1644 step 2).
///
/// <para><b>Why this exists.</b> A Razor Class Library's <c>wwwroot</c> reaches the browser at
/// <c>_content/&lt;lib&gt;/…</c> through the HOST's <em>build-time</em> static-web-assets graph —
/// i.e. through the <c>ProjectReference</c> that flipping a module to the <c>modules/</c> lane
/// deliberately removes. Nothing served <c>modules/&lt;Name&gt;/wwwroot</c>, so a flipped view pack
/// lost its CSS/JS: <c>UseStaticFiles</c> reads the host's manifests, and a standalone RCL publish
/// lays the pack's own assets at <c>wwwroot/</c> ROOT rather than at <c>_content/&lt;Name&gt;/</c> —
/// which is the URL its own components request. That mismatch is what kept Radzen, GoogleMaps and
/// Analysis referenced by the portal long after the rest of the modules flipped.</para>
///
/// <para><b>The mapping.</b> A standalone module publish produces exactly two shapes under its
/// <c>wwwroot</c>, and they need opposite treatment:</para>
/// <list type="bullet">
/// <item><b>The module's OWN assets</b> sit at the root (<c>wwwroot/GoogleMapView.razor.js</c>,
/// <c>wwwroot/&lt;Name&gt;.styles.css</c>) and are re-based to <c>_content/&lt;Name&gt;/…</c>.</item>
/// <item><b>Its DEPENDENCIES' assets</b> are already correctly namespaced
/// (<c>wwwroot/_content/&lt;Dep&gt;/…</c>) and are served at that same path — but only when the
/// host does not already provide <c>&lt;Dep&gt;</c>, so a module can never shadow a platform asset.
/// That is the same same-identity prune the DLL closure lane applies, in its request-path form, and
/// it is what keeps this off the route-collision surface of #1677/#1678/#1679.</item>
/// </list>
///
/// <para><b>Startup-time only, deliberately.</b> Assets are discovered from the folders present when
/// the app starts. A module installed at RUNTIME (a Store package) is not served until the next
/// start — the same concession the module loader itself already makes, since
/// <c>MeshBuilder.InstallAssemblies</c> is boot-only by construction and ASP.NET Core cannot accept
/// new static-asset endpoints after the <see cref="WebApplication"/> is built. Restart-as-activation
/// is the contract; inventing a second, dynamic mechanism here would outrun the loader it serves.</para>
/// </summary>
public static partial class MeshModuleStaticAssetExtensions
{
    /// <summary>The request-path prefix every Razor Class Library's assets live under.</summary>
    internal const string ContentRoot = "_content";

    /// <summary>
    /// The precompressed sibling encodings a module publish emits, best ratio first. Immutable
    /// lookup — a constant, not state.
    /// </summary>
    private static readonly (string Suffix, string Encoding)[] PrecompressedVariants =
        [(".br", "br"), (".gz", "gzip")];

    /// <summary>Resolves <c>x.js.br</c> to the media type of <c>x.js</c>. See the negotiation.</summary>
    private static readonly PrecompressedContentTypeProvider PrecompressedContentTypes = new();

    /// <summary>
    /// Registers the <see cref="ModuleStaticAssetManifest"/> — what each installed module
    /// contributes, resolved once on first use. Pair it with
    /// <see cref="UseMeshModuleStaticAssets"/>; a host that mounts without registering gets a
    /// speaking exception rather than silently unstyled modules.
    /// </summary>
    /// <param name="services">The service collection to register on.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddMeshModuleStaticAssets(this IServiceCollection services)
        => services.AddSingleton(sp => Discover(
            sp.GetServices<InstalledModuleAssembly>(),
            sp.GetRequiredService<IWebHostEnvironment>(),
            sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(MeshModuleStaticAssetExtensions))));

    /// <summary>
    /// Mounts every installed module's static web assets. Call it with the host's other
    /// <c>UseStaticFiles</c> registrations — BEFORE <c>UseRouting</c>, because these are middleware
    /// and not endpoints.
    ///
    /// <para><b>Precompressed variants.</b> A module publish emits <c>.br</c> and <c>.gz</c>
    /// siblings beside every compressible asset (measured on a real publish:
    /// <c>GoogleMapView.razor.js</c> 12,649 B → 1,945 B brotli). The host's own assets reach the
    /// browser compressed because <c>MapStaticAssets</c> serves them off an endpoint manifest that
    /// declares one endpoint per encoding — a manifest whose routes are relative to the module's
    /// OWN wwwroot root, so it cannot be re-based onto <c>_content/&lt;Name&gt;/</c> and cannot be
    /// reused here. Plain <c>UseStaticFiles</c> has no notion of a precompressed sibling and would
    /// ship every module asset at full size, so the mounts negotiate it themselves: a request that
    /// accepts an encoding whose sibling EXISTS is rewritten onto that sibling and answered with
    /// the matching <c>Content-Encoding</c>, keeping the identity file's media type.</para>
    ///
    /// <para>Where no sibling is on disk — a module folder produced by anything other than a
    /// publish — nothing is rewritten and the identity file is served exactly as before, so this
    /// is a payload optimisation in the published container and a no-op everywhere else. It is
    /// deliberately NOT a route-count property: a published layout exposes three representations
    /// per asset and a dev one exposes a single file, and asserting one number across both is how
    /// #1680 shipped a check that passed everywhere except production.</para>
    /// </summary>
    /// <param name="app">The application to add the middleware to.</param>
    /// <returns>The same <paramref name="app"/> for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="AddMeshModuleStaticAssets"/> was not called.
    /// </exception>
    public static WebApplication UseMeshModuleStaticAssets(this WebApplication app)
    {
        var manifest = app.Services.GetService<ModuleStaticAssetManifest>()
            ?? throw new InvalidOperationException(
                "UseMeshModuleStaticAssets() requires AddMeshModuleStaticAssets() on the service "
                + "collection — without it the module manifest is never built, and a flipped "
                + "module's CSS/JS would 404 at runtime with nothing in the log to say why.");

        if (manifest.Mounts.Count == 0)
            return app;

        var mounts = manifest.Mounts
            .Select(mount => (
                RequestPath: new PathString(mount.RequestPath),
                Provider: new PhysicalFileProvider(mount.PhysicalPath)))
            .ToArray();

        // A module aggregate whose host-provided @imports were stripped is served from MEMORY,
        // ahead of the mounts — the landed bytes stay exactly as published (a generation is
        // immutable), and only the response differs. Also ahead of encoding negotiation: the
        // rewritten body has no precompressed sibling to negotiate to.
        var rewrites = manifest.RewrittenStylesheets;
        if (rewrites is { Count: > 0 })
            app.Use(async (context, next) =>
            {
                if (HttpMethods.IsGet(context.Request.Method)
                    && rewrites.TryGetValue(context.Request.Path.Value ?? "", out var body))
                {
                    context.Response.ContentType = "text/css";
                    context.Response.Headers.Vary = HeaderNames.AcceptEncoding;
                    await context.Response.WriteAsync(body);
                    return;
                }
                await next();
            });

        // Encoding negotiation runs ONCE, in front of every mount: it only rewrites the path, so
        // the mounts below stay ordinary static-file middleware.
        app.Use((context, next) =>
        {
            NegotiatePrecompressed(context, mounts);
            return next();
        });

        foreach (var (requestPath, provider) in mounts)
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = provider,
                RequestPath = requestPath,
                ContentTypeProvider = PrecompressedContentTypes,
                // Every representation of these URLs varies by Accept-Encoding — including the
                // identity one, or a shared cache hands a brotli body to a client that cannot
                // decode it.
                OnPrepareResponse = static served =>
                    served.Context.Response.Headers.Vary = HeaderNames.AcceptEncoding,
            });

        return app;
    }

    /// <summary>
    /// Points the request at the best precompressed sibling the client accepts, and states the
    /// encoding it is getting. A no-op when the client accepts none, or when the publish laid down
    /// no sibling — in which case the identity file is served unchanged.
    /// </summary>
    private static void NegotiatePrecompressed(
        HttpContext context,
        (PathString RequestPath, PhysicalFileProvider Provider)[] mounts)
    {
        var path = context.Request.Path;
        if (!path.HasValue)
            return;

        foreach (var (requestPath, provider) in mounts)
        {
            // Mount request paths are distinct and StartsWithSegments is segment-aware, so at
            // most one mount owns a path — the first match is the answer either way.
            if (!path.StartsWithSegments(requestPath, out var remainder) || !remainder.HasValue)
                continue;

            var subpath = remainder.Value!;

            // A URL that already names a variant is a request for THAT representation. Its media
            // type still resolves off the identity name (below), so it needs the matching
            // Content-Encoding or the client receives compressed bytes labelled as JavaScript.
            foreach (var (suffix, encoding) in PrecompressedVariants)
                if (subpath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers.ContentEncoding = encoding;
                    return;
                }

            foreach (var (suffix, encoding) in PrecompressedVariants)
            {
                if (!Accepts(context.Request, encoding)
                    || !provider.GetFileInfo(subpath + suffix).Exists)
                    continue;

                context.Response.Headers.ContentEncoding = encoding;
                context.Request.Path = new PathString(path.Value + suffix);
                return;
            }

            return;
        }
    }

    /// <summary>
    /// Whether the client accepts <paramref name="encoding"/>, honouring an explicit
    /// <c>q=0</c> rejection the way the framework's own content negotiation does.
    /// </summary>
    private static bool Accepts(HttpRequest request, string encoding)
    {
        if (!StringWithQualityHeaderValue.TryParseList(
                request.Headers.AcceptEncoding, out var accepted))
            return false;

        foreach (var candidate in accepted)
            if (string.Equals(candidate.Value.Value, encoding, StringComparison.OrdinalIgnoreCase))
                return candidate.Quality is not 0;

        return false;
    }

    /// <summary>
    /// Gives a precompressed file the media type of what it decodes TO — <c>x.js.br</c> is
    /// <c>text/javascript</c> carried under <c>Content-Encoding: br</c>, not a media type of its
    /// own. Without this the static-file middleware finds no type for <c>.br</c> and refuses to
    /// serve it (and, worse, maps <c>.gz</c> to <c>application/x-gzip</c>, which would break a
    /// negotiated response that the browser has already decoded).
    /// </summary>
    private sealed class PrecompressedContentTypeProvider : IContentTypeProvider
    {
        private readonly FileExtensionContentTypeProvider inner = new();

        public bool TryGetContentType(string subpath, out string contentType)
        {
            foreach (var (suffix, _) in PrecompressedVariants)
                if (subpath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && inner.TryGetContentType(subpath[..^suffix.Length], out var identityType)
                    && identityType is not null)
                {
                    contentType = identityType;
                    return true;
                }

            // A genuinely-shipped archive (`data.gz`, nothing underneath it) keeps its own type.
            if (inner.TryGetContentType(subpath, out var ownType) && ownType is not null)
            {
                contentType = ownType;
                return true;
            }

            contentType = null!;
            return false;
        }
    }

    /// <summary>
    /// Resolves what each installed module contributes. Internal so the mapping rules are testable
    /// against a laid-out folder without standing up a web host.
    /// </summary>
    /// <param name="modules">The boot-installed modules to inspect.</param>
    /// <param name="environment">
    /// Supplies <see cref="IWebHostEnvironment.WebRootFileProvider"/> — what the host actually
    /// SERVES, which is what the shadowing check must ask. Outside a published layout the host's
    /// <c>_content/*</c> paths have no directory on disk at all.
    /// </param>
    /// <param name="logger">Receives one line per mounted or skipped dependency.</param>
    /// <param name="baseDirectory">
    /// FALLBACK root for the legacy <c>modules/&lt;Name&gt;/</c> layout, used only when a module's
    /// loaded assembly does not say where it came from; defaults to
    /// <see cref="AppContext.BaseDirectory"/>. The normal answer comes from the assembly itself —
    /// see <see cref="ModuleWwwrootPath"/>. A parameter only so the mapping rules can be exercised
    /// against a laid-out folder in a test.
    /// </param>
    internal static ModuleStaticAssetManifest Discover(
        IEnumerable<InstalledModuleAssembly> modules,
        IWebHostEnvironment environment,
        ILogger logger,
        string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        // 🚨 "Does the host already serve _content/<dep>?" is a question for the WEB ROOT FILE
        // PROVIDER, never for the filesystem. Only a PUBLISHED app materialises its static web
        // assets physically under wwwroot; a dev run leaves wwwroot/_content empty on disk and
        // serves those paths through the manifest-backed provider StaticWebAssetsLoader composes
        // over the physical one. A Directory.Exists probe therefore answers "the host does not
        // have it" for EVERY referenced RCL outside a published layout — and the module's copy
        // gets mounted straight on top of a live platform mapping, which is the shadowing this
        // check exists to prevent. The provider is the one source that is right in both.
        var hostAssets = environment.WebRootFileProvider;

        var mounts = new List<ModuleStaticAssetMount>();
        var stylesheets = new List<string>();
        var rewrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // First mount wins per request path — two modules carrying the SAME dependency (a shared
        // RCL) is ordinary composition, not a fault, so the second is skipped quietly rather than
        // refused. Shadowing the HOST is the case that must never happen, and that is the
        // hostAssets check below.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in modules)
        {
            var name = module.Assembly.GetName().Name;
            if (string.IsNullOrEmpty(name))
                continue;

            // BESIDE the assembly that was actually loaded — see ModuleWwwrootPath. A module that
            // still rides the app closure (the step-1 double-ship) has an EMPTY or absent folder
            // here, so it simply contributes nothing and the host's build-time manifest keeps
            // serving it. That is what makes this safe to switch on before any pack flips.
            var moduleWwwroot = ModuleWwwrootPath(module.Assembly.Location, baseDirectory, name);
            if (!Directory.Exists(moduleWwwroot))
                continue;

            // 1. The module's own assets, re-based onto its _content/<Name> namespace. A module
            //    name is unique, so this mount can never collide with another module's.
            var ownPath = $"/{ContentRoot}/{name}";
            if (claimed.Add(ownPath))
                mounts.Add(new ModuleStaticAssetMount(ownPath, moduleWwwroot));

            // The scoped-CSS bundle a standalone publish emits for the module's own .razor.css
            // files. It EXISTS (contrary to the "scoped CSS is lost" reading) — it is simply not
            // linked, because App.razor links only the host's aggregate. Hand it to the host.
            var bundlePath = Path.Combine(moduleWwwroot, $"{name}.styles.css");
            if (File.Exists(bundlePath))
            {
                var requestPath = $"{ContentRoot}/{name}/{name}.styles.css";
                stylesheets.Add(requestPath);

                // 🚨 The aggregate opens with @import lines for its DEPENDENCIES' bundles at
                // FINGERPRINTED urls, computed at MODULE-build time
                // (@import '_content/MeshWeaver.Blazor/MeshWeaver.Blazor.4p29wy9ysg.bundle.scp.css').
                // For a dependency the host provides, the host serves its own copy under ITS build's
                // fingerprint — a different string — so the import 404s on every page load, silently.
                // They are also redundant: the host already links its own aggregate, which contains
                // those very bundles. Strip exactly those, by the SAME predicate that decides not to
                // mount the dependency, so the two can never disagree about who provides what.
                var rewritten = StripHostProvidedImports(
                    File.ReadAllText(bundlePath), hostAssets, name, logger);
                if (rewritten is not null)
                    rewrites["/" + requestPath] = rewritten;
            }

            // 2. The module's DEPENDENCIES, already namespaced under its wwwroot/_content.
            var moduleContentRoot = Path.Combine(moduleWwwroot, ContentRoot);
            if (!Directory.Exists(moduleContentRoot))
                continue;

            foreach (var dependency in Directory.EnumerateDirectories(moduleContentRoot))
            {
                var dependencyName = Path.GetFileName(dependency);

                // The host wins, always. Serving a module's copy over a platform-provided one is
                // how you get two versions of the same RCL's JS answering the same URL depending
                // on middleware order — a shadowing bug that presents as a caching bug.
                if (hostAssets.GetDirectoryContents($"{ContentRoot}/{dependencyName}").Exists)
                {
                    logger.LogDebug(
                        "Module {Module}: not serving _content/{Dependency} — the host already provides it",
                        name, dependencyName);
                    continue;
                }

                var dependencyPath = $"/{ContentRoot}/{dependencyName}";
                if (!claimed.Add(dependencyPath))
                {
                    logger.LogDebug(
                        "Module {Module}: _content/{Dependency} already mounted by an earlier module",
                        name, dependencyName);
                    continue;
                }

                mounts.Add(new ModuleStaticAssetMount(dependencyPath, dependency));
                logger.LogInformation(
                    "Module {Module}: serving dependency assets at _content/{Dependency}",
                    name, dependencyName);
            }
        }

        if (mounts.Count > 0)
            logger.LogInformation(
                "Module static assets: {Count} root(s) mounted, {Stylesheets} stylesheet(s) to link",
                mounts.Count, stylesheets.Count);

        return new ModuleStaticAssetManifest(mounts, stylesheets, rewrites);
    }
    /// <summary>
    /// Where a boot-installed module's published static web assets live: <c>wwwroot</c> BESIDE the
    /// assembly the loader actually loaded, whenever that assembly sits directly inside a
    /// <c>modules/</c> tree.
    ///
    /// <para>🚨 The loaded assembly is the only thing that knows this, and asking it is what makes
    /// the rule survive the two ways the tree moved out from under a name-based path. Landing
    /// writes a fresh GENERATION directory per version (<c>modules/&lt;Name&gt;@&lt;id&gt;/</c>) and
    /// moves the activation pointer — the legacy fixed <c>modules/&lt;Name&gt;/</c> folder is not
    /// written at all any more (#1949) — and it writes into the deployment's WRITABLE, pod-shared
    /// module root (<c>ModuleRoot</c>, <c>/data</c> on AKS), not the read-only app directory. A
    /// path built from <see cref="AppContext.BaseDirectory"/> plus the module NAME misses on both
    /// counts, and it misses SILENTLY: the module loads, its components render, and every asset
    /// they request 404s.</para>
    ///
    /// <para>The "inside a <c>modules/</c> tree" test is what keeps a module that still rides the
    /// APP CLOSURE contributing nothing: its assembly's directory is the app folder, whose
    /// <c>wwwroot</c> is the HOST's — mounting that under <c>_content/&lt;Name&gt;</c> would serve
    /// the whole host web root through a module namespace. Such a module falls back to the legacy
    /// name-based path, which is absent for it, so it is skipped exactly as before.</para>
    /// </summary>
    /// <param name="assemblyLocation">The loaded module assembly's file location (empty for an
    /// assembly with no file backing).</param>
    /// <param name="baseDirectory">Fallback root for the legacy <c>modules/&lt;Name&gt;/</c> layout.</param>
    /// <param name="moduleName">The module's assembly name.</param>
    /// <returns>The absolute <c>wwwroot</c> path to inspect (it may not exist).</returns>
    internal static string ModuleWwwrootPath(
        string? assemblyLocation, string baseDirectory, string moduleName)
    {
        var assemblyDirectory = string.IsNullOrEmpty(assemblyLocation)
            ? null
            : Path.GetDirectoryName(assemblyLocation);
        var parent = string.IsNullOrEmpty(assemblyDirectory)
            ? null
            : Path.GetFileName(Path.GetDirectoryName(assemblyDirectory));
        return string.Equals(parent, "modules", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(assemblyDirectory!, "wwwroot")
            : Path.Combine(baseDirectory, "modules", moduleName, "wwwroot");
    }

    /// <summary>
    /// Removes the <c>@import</c> lines of a module scoped-CSS aggregate that name a dependency the
    /// HOST provides, returning the rewritten body — or <c>null</c> when nothing needed removing, so
    /// the ordinary static-file path keeps serving the file untouched.
    ///
    /// <para>Those imports carry the fingerprint computed by the MODULE's build; the host serves its
    /// own copy of the same dependency under ITS build's fingerprint, so the URL cannot resolve and
    /// the browser 404s once per page load with nothing in any log. They are redundant besides — the
    /// host links its own aggregate, which already contains those bundles. An import for a dependency
    /// the host does NOT provide is KEPT: the module's own <c>wwwroot/_content/&lt;Dep&gt;/</c> carries
    /// that bundle at the very fingerprint the import names, both produced by the same publish.</para>
    /// </summary>
    internal static string? StripHostProvidedImports(
        string css, IFileProvider hostAssets, string moduleName, ILogger logger)
    {
        var lines = css.Split('\n');
        var kept = new List<string>(lines.Length);
        var dropped = new List<string>();
        foreach (var line in lines)
        {
            var match = ImportedContentBundle().Match(line);
            if (match.Success)
            {
                var dependency = match.Groups["dep"].Value;
                if (hostAssets.GetDirectoryContents($"{ContentRoot}/{dependency}").Exists)
                {
                    dropped.Add(dependency);
                    continue;
                }
            }
            kept.Add(line);
        }

        if (dropped.Count == 0)
            return null;

        logger.LogInformation(
            "Module {Module}: dropped {Count} fingerprinted @import(s) for host-provided "
            + "dependencies ({Dependencies}) — the host links its own aggregate, and the module's "
            + "fingerprints do not match the host's copies.",
            moduleName, dropped.Count, string.Join(", ", dropped));
        return string.Join('\n', kept);
    }

    /// <summary>Matches <c>@import '_content/&lt;Dep&gt;/…';</c> in a scoped-CSS aggregate.</summary>
    [GeneratedRegex("""^\s*@import\s+['"]_content/(?<dep>[^/'"]+)/[^'"]*['"]\s*;""")]
    private static partial Regex ImportedContentBundle();

}

/// <summary>One mounted asset root: everything under <paramref name="PhysicalPath"/> is served at
/// <paramref name="RequestPath"/>.</summary>
/// <param name="RequestPath">Rooted request path, e.g. <c>/_content/MeshWeaver.Blazor.GoogleMaps</c>.</param>
/// <param name="PhysicalPath">Absolute directory backing it.</param>
public sealed record ModuleStaticAssetMount(string RequestPath, string PhysicalPath);

/// <summary>
/// What the module static-asset lane contributes: the roots to mount, and the scoped-CSS bundles a
/// host must link itself.
///
/// <para>A REFERENCED Razor Class Library's <c>.razor.css</c> is bundled into the host's
/// <c>&lt;App&gt;.styles.css</c> at host build, and <c>App.razor</c> links only that aggregate. A
/// module that ships via <c>modules/</c> is outside that build, so its bundle is published
/// standalone and nothing links it — which renders the module's components unstyled while every
/// script still works, the most confusing possible half-failure. Hosts render one
/// <c>&lt;link&gt;</c> per <paramref name="Stylesheets"/> entry to close that gap.</para>
/// </summary>
/// <param name="Mounts">Asset roots to serve, in module order.</param>
/// <param name="Stylesheets">
/// Root-relative paths (<c>_content/&lt;Name&gt;/&lt;Name&gt;.styles.css</c>), in module order.
/// </param>
public sealed record ModuleStaticAssetManifest(
    IReadOnlyList<ModuleStaticAssetMount> Mounts,
    IReadOnlyList<string> Stylesheets,
    IReadOnlyDictionary<string, string>? RewrittenStylesheets = null);
