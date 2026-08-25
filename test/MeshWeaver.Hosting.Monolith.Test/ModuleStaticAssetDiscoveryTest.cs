using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Mesh;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the mapping rules of the module static-asset lane (#1724) — the half of a
/// <c>modules/&lt;Name&gt;/</c> flip that a <c>dotnet build</c> cannot see.
///
/// <para>A flipped Razor Class Library's assets are published to <c>modules/&lt;Name&gt;/wwwroot</c>
/// with its OWN files at the root and its DEPENDENCIES' already namespaced under
/// <c>_content/&lt;Dep&gt;/</c>. Those two shapes need opposite treatment, and getting either wrong
/// fails at RUNTIME as a 404 for a script the component hard-codes — invisible to every compile
/// gate, which is exactly why it is pinned here rather than left to a boot smoke test.</para>
///
/// <para>Exercised against a laid-out folder rather than a live host: the rules are filesystem
/// shape in, mount list out, and a real <c>WebApplication</c> would test ASP.NET's static-file
/// middleware instead of the mapping this owns.</para>
/// </summary>
public class ModuleStaticAssetDiscoveryTest : IDisposable
{
    // Two real assemblies, used only for their NAMES — Discover reads
    // Assembly.GetName().Name to find modules/<Name>/, and never loads anything.
    private static readonly System.Reflection.Assembly WithAssets =
        typeof(MeshModuleStaticAssetExtensions).Assembly;   // MeshWeaver.Hosting.AspNetCore
    private static readonly System.Reflection.Assembly WithoutAssets =
        typeof(InstalledModuleAssembly).Assembly;           // MeshWeaver.Mesh.Contract

    private static string ModuleName => WithAssets.GetName().Name!;

    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"mw-modassets-{Guid.NewGuid():N}");

    private string ModuleWwwroot => Path.Combine(root, "modules", ModuleName, "wwwroot");
    private string HostWebRoot => Path.Combine(root, "wwwroot");

    public ModuleStaticAssetDiscoveryTest()
    {
        // The module's OWN assets — published at the wwwroot ROOT, which is precisely why they
        // cannot be served as-is: the component requests _content/<Name>/GoogleMapView.razor.js.
        Directory.CreateDirectory(ModuleWwwroot);
        File.WriteAllText(Path.Combine(ModuleWwwroot, "PackView.razor.js"), "export {};");
        File.WriteAllText(Path.Combine(ModuleWwwroot, $"{ModuleName}.styles.css"), ".x{}");

        // A dependency the host does NOT provide → the module's copy is the only one.
        Directory.CreateDirectory(Path.Combine(ModuleWwwroot, "_content", "PrivateDep"));
        File.WriteAllText(
            Path.Combine(ModuleWwwroot, "_content", "PrivateDep", "dep.js"), "export {};");

        // A dependency the host ALREADY serves → must be skipped, or two copies of the same RCL
        // answer the same URL depending on middleware order.
        Directory.CreateDirectory(Path.Combine(ModuleWwwroot, "_content", "SharedDep"));
        File.WriteAllText(
            Path.Combine(ModuleWwwroot, "_content", "SharedDep", "shared.js"), "export {};");
        Directory.CreateDirectory(Path.Combine(HostWebRoot, "_content", "SharedDep"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The PUBLISHED shape: the host's static web assets are materialised under wwwroot,
    /// so the web root and what the host serves are the same directory.</summary>
    private ModuleStaticAssetManifest Discover(params System.Reflection.Assembly[] modules)
        => Discover(
            new StubWebHostEnvironment(HostWebRoot, new PhysicalFileProvider(HostWebRoot)),
            modules);

    private ModuleStaticAssetManifest Discover(
        IWebHostEnvironment environment, params System.Reflection.Assembly[] modules)
        => MeshModuleStaticAssetExtensions.Discover(
            modules.Select(a => new InstalledModuleAssembly(a)),
            environment,
            NullLogger.Instance,
            root);

    /// <summary>
    /// The module's own assets are re-based onto <c>_content/&lt;Name&gt;</c> — the URL its
    /// components actually request — rather than left at the publish's wwwroot root.
    /// </summary>
    [Fact]
    public void OwnAssets_AreRebasedOntoTheModuleContentNamespace()
    {
        var manifest = Discover(WithAssets);

        var own = manifest.Mounts.Single(m => m.RequestPath == $"/_content/{ModuleName}");
        own.PhysicalPath.Should().Be(ModuleWwwroot);
        File.Exists(Path.Combine(own.PhysicalPath, "PackView.razor.js")).Should().BeTrue(
            "the mount must expose the module's own root-level assets");
    }

    /// <summary>
    /// A dependency the host does not carry is served from the module, at its OWN namespace —
    /// NOT nested under the module's (which is the URL nobody requests).
    /// </summary>
    [Fact]
    public void PrivateDependency_IsServedAtItsOwnContentNamespace()
    {
        var manifest = Discover(WithAssets);

        manifest.Mounts.Should().ContainSingle(m => m.RequestPath == "/_content/PrivateDep")
            .Which.PhysicalPath.Should().Be(
                Path.Combine(ModuleWwwroot, "_content", "PrivateDep"));

        manifest.Mounts.Should().NotContain(
            m => m.RequestPath.Contains($"/_content/{ModuleName}/_content"),
            "a dependency must keep its own _content/<Dep> namespace, not be nested under the module's");
    }

    /// <summary>
    /// 🚨 The host always wins. A module carrying a dependency the platform already serves must NOT
    /// mount its copy — otherwise two builds of the same RCL answer one URL and which one you get
    /// depends on middleware order, a shadowing bug that presents as a caching bug.
    /// </summary>
    [Fact]
    public void DependencyTheHostAlreadyServes_IsNotMounted()
    {
        var manifest = Discover(WithAssets);

        manifest.Mounts.Should().NotContain(m => m.RequestPath == "/_content/SharedDep",
            "the host's copy is authoritative — the module lane must never shadow a platform asset");
    }

    /// <summary>
    /// 🚨 …and the host still wins when it serves that dependency WITHOUT a directory on disk.
    /// Only a published layout materialises <c>wwwroot/_content/&lt;Dep&gt;</c>; a dev run serves the
    /// very same URLs out of the manifest-backed provider composed over the web root, with nothing
    /// under <c>wwwroot</c> to stat. A <c>Directory.Exists</c> probe concludes "the host does not
    /// have it" for EVERY referenced RCL there and mounts the module's copy over a live platform
    /// mapping — so the shadowing check has to ask what the host SERVES, not what it stores.
    /// </summary>
    [Fact]
    public void DependencyTheHostServesWithoutAPhysicalDirectory_IsNotMounted()
    {
        var emptyWebRoot = Path.Combine(root, "wwwroot-dev");
        Directory.CreateDirectory(emptyWebRoot);

        // What the host SERVES — the dev stand-in for the composed static-web-assets provider.
        var servedRoot = Path.Combine(root, "host-served");
        Directory.CreateDirectory(Path.Combine(servedRoot, "_content", "SharedDep"));

        var manifest = Discover(
            new StubWebHostEnvironment(emptyWebRoot, new PhysicalFileProvider(servedRoot)),
            WithAssets);

        Directory.Exists(Path.Combine(emptyWebRoot, "_content", "SharedDep")).Should().BeFalse(
            "the premise: outside a published layout the host's asset has no directory to find");
        manifest.Mounts.Should().NotContain(m => m.RequestPath == "/_content/SharedDep",
            "the host serves it through its file provider, so the module must not shadow it");

        // …while a dependency the host genuinely lacks is still served from the module.
        manifest.Mounts.Should().Contain(m => m.RequestPath == "/_content/PrivateDep");
    }

    /// <summary>
    /// The scoped-CSS bundle is surfaced for the host to link. It EXISTS in a standalone publish
    /// (the "scoped CSS is lost on flip" reading is wrong) — it is simply not folded into the
    /// host's aggregate, so nothing links it and the module renders unstyled while its JS works.
    /// </summary>
    [Fact]
    public void ScopedStyleBundle_IsSurfacedForTheHostToLink()
    {
        var manifest = Discover(WithAssets);

        manifest.Stylesheets.Should().ContainSingle()
            .Which.Should().MatchRegex(
                $"^_content/{Regex.Escape(ModuleName)}/{Regex.Escape(ModuleName)}\\.[0-9a-f]{{16}}\\.styles\\.css$",
                "the ONE module asset whose URL the platform authors carries the body's "
                + "content fingerprint, so it can be served immutable");
    }

    /// <summary>
    /// 🚨 The fingerprint is over the BODY, so it is what makes <c>immutable</c> honest — and it is
    /// the only reason a module upgrade costs a viewer nothing rather than one conditional GET per
    /// linked bundle per page render (#1974's last open gap).
    ///
    /// <para>Two properties, and the pair is the point: a changed bundle gets a DIFFERENT URL (so
    /// the upgrade is picked up instantly, with no stale-cache window), and an unchanged one gets
    /// the SAME URL (so a republish that changed nothing does not invalidate every viewer's copy —
    /// which is exactly what a generation- or timestamp-derived token would do).</para>
    /// </summary>
    [Fact]
    public void StyleBundleFingerprint_TracksTheBody_NotTheLanding()
    {
        var first = Discover(WithAssets).Stylesheets.Single();

        // Re-landing the SAME bytes (a fresh generation directory, new mtimes) — same URL.
        File.WriteAllText(Path.Combine(ModuleWwwroot, $"{ModuleName}.styles.css"), ".x{}");
        Discover(WithAssets).Stylesheets.Single().Should().Be(first,
            "identical bytes must keep the URL, or every republish invalidates every cached copy");

        // Different bytes — different URL.
        File.WriteAllText(Path.Combine(ModuleWwwroot, $"{ModuleName}.styles.css"), ".x{color:red}");
        Discover(WithAssets).Stylesheets.Single().Should().NotBe(first,
            "an upgraded bundle must reach the browser without waiting for a cache to expire");
    }

    /// <summary>
    /// The fingerprinted URL is answered <c>immutable</c>; the plain <c>&lt;Name&gt;.styles.css</c>
    /// is answered too — same body, but <c>no-cache</c>, because what THAT URL means changes with
    /// every module upgrade.
    ///
    /// <para>Serving the plain path is not politeness: leaving it to fall through to the mount
    /// would serve the LANDED file, whose host-provided <c>@import</c>s name fingerprints only the
    /// module's own build ever had — a silent 404 per page load, which is the defect
    /// <c>StripHostProvidedImports</c> exists to remove.</para>
    /// </summary>
    [Fact]
    public void BothStyleBundleUrls_AreServedFromMemory_WithOppositeCachingPromises()
    {
        var manifest = Discover(WithAssets);
        var fingerprinted = "/" + manifest.Stylesheets.Single();
        var plain = $"/_content/{ModuleName}/{ModuleName}.styles.css";

        manifest.StylesheetBodies.Should().NotBeNull();
        manifest.StylesheetBodies![fingerprinted].Should()
            .Be(new ModuleStylesheetResponse(".x{}", true));
        manifest.StylesheetBodies[plain].Should()
            .Be(new ModuleStylesheetResponse(".x{}", false));
    }


    /// <summary>
    /// A pack with collocated JS but no <c>.razor.css</c> of its own still emits an aggregate — one
    /// made of NOTHING but the dependency imports (211 bytes, measured for AppleMaps and
    /// OpenStreetMap when they flipped, #1974). Once the host-provided ones are stripped there is
    /// nothing left, so the host must not link it: every page render would fetch an empty file.
    ///
    /// <para>The emptiness is only knowable AFTER stripping — the file on disk is 211 bytes — which
    /// is why the decision cannot be a test on the landed bytes.</para>
    /// </summary>
    [Fact]
    public void AggregateOfNothingButHostProvidedImports_IsNotLinked()
    {
        // The host provides this dependency, so the module's import of it is the strippable kind.
        File.WriteAllText(
            Path.Combine(ModuleWwwroot, $"{ModuleName}.styles.css"),
            "@import '_content/SharedDep/SharedDep.abc123.bundle.scp.css';\n");

        var manifest = Discover(WithAssets);

        manifest.Stylesheets.Should().BeEmpty();
        // …and the module still contributes its ordinary asset mounts: the stylesheet decision
        // must not short-circuit the dependency walk that follows it.
        manifest.Mounts.Should().NotBeEmpty();
    }
    /// <summary>
    /// A module with no <c>modules/&lt;Name&gt;/wwwroot</c> contributes nothing and does not throw.
    /// That is every module today — the step-1 double-ship prunes the folder empty — so this is
    /// what makes the lane safe to switch on BEFORE any pack flips.
    /// </summary>
    [Fact]
    public void ModuleWithoutAnAssetFolder_ContributesNothing()
    {
        var manifest = Discover(WithoutAssets);

        manifest.Mounts.Should().BeEmpty();
        manifest.Stylesheets.Should().BeEmpty();
    }

    // ---- #1949: the asset folder comes from the LOADED ASSEMBLY, not from a name-built path ----

    /// <summary>
    /// A module landed the way landing lands today — into a GENERATION directory
    /// (<c>modules/&lt;Name&gt;@&lt;id&gt;/</c>) on the deployment's writable module ROOT, which is
    /// not the app directory — must have its assets found there. A path built from
    /// <c>AppContext.BaseDirectory</c> + the module NAME misses on both counts, and misses
    /// SILENTLY: the module loads, its components render, and every asset they request 404s.
    /// </summary>
    [Fact]
    public void GenerationLandedModule_TakesItsAssetsFromTheLoadedAssemblysOwnFolder()
    {
        // A landed module on a shared volume: /data/modules/<Name>@<gen>/<Name>.dll — a different
        // ROOT and a different DIRECTORY SHAPE from the legacy modules/<Name>/ under the app.
        var landed = Path.Combine("/data", "modules", $"{ModuleName}@a1b2c3d4e");

        MeshModuleStaticAssetExtensions.ModuleWwwrootPath(
                Path.Combine(landed, $"{ModuleName}.dll"),
                AppContext.BaseDirectory,
                ModuleName)
            .Should().Be(Path.Combine(landed, "wwwroot"));

        // The legacy fixed folder still works — same rule, it is simply the directory the
        // assembly happens to sit in.
        var legacy = Path.Combine("/data", "modules", ModuleName);
        MeshModuleStaticAssetExtensions.ModuleWwwrootPath(
                Path.Combine(legacy, $"{ModuleName}.dll"), AppContext.BaseDirectory, ModuleName)
            .Should().Be(Path.Combine(legacy, "wwwroot"));
    }

    /// <summary>
    /// 🚨 A module still riding the APP CLOSURE must contribute NOTHING. Its assembly sits in the
    /// app folder, whose <c>wwwroot</c> is the HOST's — mounting that under
    /// <c>_content/&lt;Name&gt;</c> would serve the entire host web root through a module
    /// namespace. Only an assembly sitting directly inside a <c>modules/</c> tree supplies its own
    /// folder; everything else falls back to the (absent) legacy name-based path.
    /// </summary>
    [Fact]
    public void AppClosureModule_NeverMountsTheHostsOwnWwwroot()
    {
        MeshModuleStaticAssetExtensions.ModuleWwwrootPath(
                Path.Combine("/app", $"{ModuleName}.dll"), "/app", ModuleName)
            .Should().Be(Path.Combine("/app", "modules", ModuleName, "wwwroot"));

        // No file backing at all (a dynamic assembly) degrades the same way.
        MeshModuleStaticAssetExtensions.ModuleWwwrootPath("", "/app", ModuleName)
            .Should().Be(Path.Combine("/app", "modules", ModuleName, "wwwroot"));
    }

    /// <summary>
    /// Two modules carrying the SAME private dependency is ordinary composition, not a fault: the
    /// first mount wins and the second is skipped, rather than the loud refusal a genuine route
    /// collision gets (#1677/#1678/#1679). Refusing here would make shared RCLs unusable.
    /// </summary>
    [Fact]
    public void SameDependencyFromTwoModules_MountsOnce()
    {
        var second = WithoutAssets.GetName().Name!;
        var secondWwwroot = Path.Combine(root, "modules", second, "wwwroot");
        Directory.CreateDirectory(Path.Combine(secondWwwroot, "_content", "PrivateDep"));
        File.WriteAllText(
            Path.Combine(secondWwwroot, "_content", "PrivateDep", "dep.js"), "export {};");

        var manifest = Discover(WithAssets, WithoutAssets);

        manifest.Mounts.Count(m => m.RequestPath == "/_content/PrivateDep").Should().Be(1);
    }

    /// <summary>
    /// End-to-end through real ASP.NET middleware: a file under <c>modules/&lt;Name&gt;/wwwroot</c>
    /// is actually SERVED at <c>_content/&lt;Name&gt;/…</c>, and a path outside the lane still 404s.
    ///
    /// <para>The mapping tests above prove the manifest; this proves the mount, which is the half
    /// that a wrong <c>RequestPath</c> or a middleware-ordering mistake breaks. Without it the
    /// suite could be green while every flipped pack 404s in the browser — the exact failure the
    /// lane exists to prevent.</para>
    /// </summary>
    [Fact]
    public async Task MountedAsset_IsServedOverHttp()
    {
        await using var app = BuildHost();
        await app.StartAsync();

        var client = app.GetTestClient();

        var served = await client.GetAsync($"/_content/{ModuleName}/PackView.razor.js");
        served.StatusCode.Should().Be(HttpStatusCode.OK,
            "the module's own asset must answer at the URL its component hard-codes");
        (await served.Content.ReadAsStringAsync()).Should().Be(IdentityBody);

        var dependency = await client.GetAsync("/_content/PrivateDep/dep.js");
        dependency.StatusCode.Should().Be(HttpStatusCode.OK,
            "a dependency the host does not carry is served from the module");

        var shadowed = await client.GetAsync("/_content/SharedDep/shared.js");
        shadowed.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the module's copy of a host-provided dependency must never be mounted");

        await app.StopAsync();
    }

    /// <summary>
    /// PUBLISHED layout: a module publish lays a <c>.br</c> beside every compressible asset
    /// (measured: <c>GoogleMapView.razor.js</c> 12,649 B → 1,945 B). Plain <c>UseStaticFiles</c>
    /// ignores that sibling and ships the full-size file, so the mount negotiates it — the client
    /// gets the compressed representation under the IDENTITY media type, which is the whole point:
    /// <c>Content-Encoding</c> says how to decode it, <c>Content-Type</c> says what it then is.
    /// </summary>
    [Fact]
    public async Task PrecompressedSibling_IsServedWhenTheClientAcceptsIt()
    {
        var brotli = Compress(IdentityBody);
        await File.WriteAllBytesAsync(
            Path.Combine(ModuleWwwroot, "PackView.razor.js.br"), brotli);

        await using var app = BuildHost();
        await app.StartAsync();

        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/_content/{ModuleName}/PackView.razor.js");
        request.Headers.AcceptEncoding.ParseAdd("br");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().Equal(new[] { "br" },
            "the response must say which encoding it carries, or the browser cannot decode it");
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript",
            "the media type is the identity file's — .br is an encoding, not a type");
        response.Headers.Vary.Should().Contain("Accept-Encoding",
            "a shared cache must not hand this body to a client that did not ask for brotli");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(brotli);

        await app.StopAsync();
    }

    /// <summary>
    /// DEV/CI layout: nothing published the module, so no <c>.br</c> exists next to the asset and
    /// negotiation must fall straight through to the identity file rather than 404 on a sibling
    /// that was never laid down.
    ///
    /// <para>Deliberately asserted as its own behaviour rather than by comparing route counts
    /// across the two worlds: a published layout exposes three representations per asset and a dev
    /// one exposes a single file, and a whole-table assertion over that is exactly how #1680
    /// shipped a check that passed everywhere except production.</para>
    /// </summary>
    [Fact]
    public async Task WithNoPrecompressedSibling_TheIdentityFileIsServed()
    {
        await using var app = BuildHost();
        await app.StartAsync();

        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/_content/{ModuleName}/PackView.razor.js");
        request.Headers.AcceptEncoding.ParseAdd("br");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().BeEmpty(
            "there is no compressed representation to announce");
        (await response.Content.ReadAsStringAsync()).Should().Be(IdentityBody);

        await app.StopAsync();
    }

    /// <summary>
    /// 🚨 Every module URL STATES its caching policy over the wire, and the two states are
    /// opposite — which is the whole of #1974's fingerprinting gap, asserted where a browser would
    /// see it rather than on the manifest.
    ///
    /// <para><b>The fingerprinted bundle</b> is <c>immutable</c>: its URL encodes its bytes, so a
    /// viewer who has it never asks again and an upgrade arrives instantly under a new URL.
    /// <b>Everything else</b> is <c>no-cache</c> — a pack hard-codes
    /// <c>_content/&lt;Name&gt;/&lt;file&gt;</c> in its own component source, so the host cannot
    /// fingerprint it and must not claim it is immutable.</para>
    ///
    /// <para>The <c>no-cache</c> is not a downgrade from silence, it is the fix for it: with NO
    /// <c>Cache-Control</c> (what plain <c>UseStaticFiles</c> emits) a browser applies HEURISTIC
    /// freshness off <c>Last-Modified</c> and may serve an upgraded module's JS from cache without
    /// revalidating at all. Stating <c>no-cache</c> makes the conditional GET happen, so the new
    /// bytes cannot be missed.</para>
    /// </summary>
    [Fact]
    public async Task ModuleAssets_StateTheirCachingPolicy_ImmutableOnlyWhereFingerprinted()
    {
        var manifest = Discover(WithAssets);
        var fingerprinted = "/" + manifest.Stylesheets.Single();

        await using var app = BuildHost();
        await app.StartAsync();
        var client = app.GetTestClient();

        var bundle = await client.GetAsync(fingerprinted);
        bundle.StatusCode.Should().Be(HttpStatusCode.OK,
            "App.razor links exactly this URL off the manifest");
        bundle.Headers.CacheControl!.ToString().Should().Contain("immutable",
            "the URL carries the body's content hash, so the bytes behind it can never change");

        var plain = await client.GetAsync($"/_content/{ModuleName}/{ModuleName}.styles.css");
        plain.StatusCode.Should().Be(HttpStatusCode.OK,
            "the plain path is answered from memory too — never from the landed file, whose "
            + "host-provided @imports point at fingerprints only the module's build ever had");
        plain.Headers.CacheControl!.NoCache.Should().BeTrue(
            "what THIS URL means changes with every module upgrade");

        var script = await client.GetAsync($"/_content/{ModuleName}/PackView.razor.js");
        script.StatusCode.Should().Be(HttpStatusCode.OK);
        script.Headers.CacheControl!.NoCache.Should().BeTrue(
            "a pack authors this URL itself, so the host cannot fingerprint it — and saying "
            + "nothing would let a heuristic cache serve the pre-upgrade file");

        await app.StopAsync();
    }

    private const string IdentityBody = "export {};";

    private static byte[] Compress(string content)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal))
            brotli.Write(System.Text.Encoding.UTF8.GetBytes(content));
        return output.ToArray();
    }

    private WebApplication BuildHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(
            MeshModuleStaticAssetExtensions.Discover(
                [new InstalledModuleAssembly(WithAssets)],
                new StubWebHostEnvironment(HostWebRoot, new PhysicalFileProvider(HostWebRoot)),
                NullLogger.Instance,
                root));

        var app = builder.Build();
        app.UseMeshModuleStaticAssets();
        return app;
    }

    /// <summary>
    /// A host environment whose WEB ROOT and whose SERVED assets can be pointed at different
    /// places — which is not an artificial split but the ordinary dev shape: outside a published
    /// layout the host's <c>_content/*</c> paths exist only in the manifest-backed provider
    /// <c>StaticWebAssetsLoader</c> composes over the physical web root.
    /// </summary>
    private sealed class StubWebHostEnvironment(string webRootPath, IFileProvider served)
        : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = webRootPath;
        public IFileProvider WebRootFileProvider { get; set; } = served;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = webRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = "Development";
    }
}
