using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

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
public static class MeshModuleStaticAssetExtensions
{
    /// <summary>The request-path prefix every Razor Class Library's assets live under.</summary>
    internal const string ContentRoot = "_content";

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

        foreach (var mount in manifest.Mounts)
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(mount.PhysicalPath),
                RequestPath = mount.RequestPath,
            });

        return app;
    }

    /// <summary>
    /// Resolves what each installed module contributes. Internal so the mapping rules are testable
    /// against a laid-out folder without standing up a web host.
    /// </summary>
    /// <param name="modules">The boot-installed modules to inspect.</param>
    /// <param name="environment">Supplies the host's web root, used for the shadowing check.</param>
    /// <param name="logger">Receives one line per mounted or skipped dependency.</param>
    /// <param name="baseDirectory">
    /// Where <c>modules/</c> lives; defaults to <see cref="AppContext.BaseDirectory"/>, which is
    /// what <c>MeshBuilder.ResolveModulePath</c> probes. A parameter only so the mapping rules can
    /// be exercised against a laid-out folder in a test.
    /// </param>
    internal static ModuleStaticAssetManifest Discover(
        IEnumerable<InstalledModuleAssembly> modules,
        IWebHostEnvironment environment,
        ILogger logger,
        string? baseDirectory = null)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        // The host's own _content/<dep> roots. A published app materialises them physically under
        // wwwroot, so their presence is the cheap, checkable form of "the platform already serves
        // this dependency" — and the reason a module's copy is skipped rather than racing it.
        var hostContentRoot = string.IsNullOrEmpty(environment.WebRootPath)
            ? null
            : Path.Combine(environment.WebRootPath, ContentRoot);

        var mounts = new List<ModuleStaticAssetMount>();
        var stylesheets = new List<string>();
        // First mount wins per request path — two modules carrying the SAME dependency (a shared
        // RCL) is ordinary composition, not a fault, so the second is skipped quietly rather than
        // refused. Shadowing the HOST is the case that must never happen, and that is the
        // hostContentRoot check below.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in modules)
        {
            var name = module.Assembly.GetName().Name;
            if (string.IsNullOrEmpty(name))
                continue;

            // AppContext.BaseDirectory, matching MeshBuilder.ResolveModulePath — a module that
            // still rides the app closure (the step-1 double-ship) has an EMPTY or absent folder
            // here, so it simply contributes nothing and the host's build-time manifest keeps
            // serving it. That is what makes this safe to switch on before any pack flips.
            var moduleWwwroot = Path.Combine(baseDirectory, "modules", name, "wwwroot");
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
            if (File.Exists(Path.Combine(moduleWwwroot, $"{name}.styles.css")))
                stylesheets.Add($"{ContentRoot}/{name}/{name}.styles.css");

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
                if (hostContentRoot is not null
                    && Directory.Exists(Path.Combine(hostContentRoot, dependencyName)))
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

        return new ModuleStaticAssetManifest(mounts, stylesheets);
    }
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
    IReadOnlyList<string> Stylesheets);
