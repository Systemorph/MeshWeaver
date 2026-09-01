using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.Compiler;

/// <summary>
/// THE framework build identity every compiled NodeType release is pinned to (issue #1660 WS3) —
/// the value <see cref="FrameworkVersion"/> resolves and every other reading (the assembly-store
/// filename tag, <c>CompiledFrameworkVersion</c> stamps, the CI bake artifact key,
/// <c>PrebuiltAssemblySeeder</c>'s adoption gate, the build-protocol fingerprint) flows from. One
/// identity, one resolution — a producer and a consumer can never disagree about what "the
/// framework" is.
///
/// <para><b>Three schemes, in resolution order:</b></para>
/// <list type="number">
/// <item><description><b>The API-SURFACE identity (<c>s&lt;hash&gt;</c>)</b> — the default for
/// every host that ships a <c>meshweaver-surface.manifest</c> (the portals and the CI bake host;
/// written at build time by the <c>_MeshWeaverWriteSurfaceManifest</c> target in
/// <c>Directory.Build.props</c>, opt-in via <c>$(MeshWeaverSurfaceManifest)</c>). "Rebuild only
/// when we need to": a NodeType's bytes go stale only when the API SURFACE they compiled against
/// changed — a breaking change — not on internal-only framework commits. The manifest records,
/// per compile reference, the SHA-256 of its REFERENCE ASSEMBLY (the compiler's own definition of
/// an assembly's surface: byte-stable under body-only and private-member edits, changed by any
/// public/protected — and, for IVT'd assemblies, internal — surface change; proven empirically,
/// see the test + the props target comment). The identity hashes the sorted
/// <c>(name, surface-id)</c> pairs of the CANONICAL content-surface set
/// (<see cref="ContentSurfaceAssemblies"/>), with the generator-bearing exceptions
/// (<see cref="FullMvidAssemblies"/>) contributing their FULL implementation MVID instead — their
/// code shapes the GENERATED INPUT of every NodeType compile, so a body-only change there changes
/// what gets compiled without any API change.</description></item>
/// <item><description><b>The commit identity (<c>g&lt;sha&gt;</c>)</b> — the
/// <c>AssemblyMetadata("MeshWeaverFrameworkIdentity")</c> stamp CI bakes into every assembly
/// (clean checkout at a named commit). The fallback for CI-built processes WITHOUT a surface
/// manifest (test hosts on CI), and retained everywhere as DIAGNOSTIC PROVENANCE — logged beside
/// the surface identity so an operator can always answer "which commit built this".</description></item>
/// <item><description><b>The anchor MVID</b> — local builds with neither: the
/// <c>MeshWeaver.Compiler</c> assembly's MVID, a content identity of the toolchain, exact for a
/// dirty working tree. Deliberately a SINGLE file's identity so a producer can attribute it
/// without loading anything (<c>MeshWeaver.Plugin.Build.FrameworkIdentity.ReadIdentity</c>).
/// The fallback governs only manifest-less processes — test hosts and ad-hoc tools; every host
/// that compiles against a PERSISTENT assembly store (the portals, the CI bake host,
/// mw-plugin-test) ships a surface manifest and resolves the surface identity, locally
/// included.</description></item>
/// </list>
///
/// <para><b>Why a canonical LIST and not the ambient TPA:</b> the bake host (mw-plugin-test) and
/// the portal are different apps with different closures (the portal adds Blazor/Orleans/hosting
/// assemblies the tester never loads — 26 of them as of 2026-08), so any host-derived set would
/// give the two DIFFERENT identities and every bake would decline. The list is the tester's
/// framework closure minus its host assemblies — exactly the surface shipped content can compile
/// against, enforced by the CI gate itself (content referencing anything outside it fails the
/// bake gate). Portal-only assemblies are deliberately outside the staleness key: shipped content
/// provably does not reference them, and the gate keeps it that way.</para>
/// </summary>
public static class FrameworkBuildIdentity
{
    /// <summary>
    /// The <see cref="AssemblyMetadataAttribute"/> key carrying the CI commit identity — stamped
    /// into every assembly by <c>Directory.Build.props</c> (target <c>AddCommitHashMetadata</c>)
    /// when <c>CIRun=true</c> on GitHub Actions. The value shape is <c>g&lt;full-commit-sha&gt;</c>.
    /// Since the surface identity landed this is the FALLBACK key (manifest-less CI processes) and
    /// the provenance everyone logs.
    /// </summary>
    public const string MetadataKey = "MeshWeaverFrameworkIdentity";

    /// <summary>
    /// The surface manifest's file name, resolved beside the entry assembly
    /// (<see cref="AppContext.BaseDirectory"/>). One line per compile reference:
    /// <c>&lt;AssemblySimpleName&gt;=&lt;SHA-256 of its reference assembly&gt;</c>.
    /// </summary>
    public const string SurfaceManifestFileName = "meshweaver-surface.manifest";

    /// <summary>
    /// 🚨 The CANONICAL content-surface assembly set — the ONE list both the CI bake host and
    /// every portal hash over, ordinal-sorted. It is the transitive MeshWeaver.* closure of
    /// <c>tools/MeshWeaver.PluginTester</c> (the content gate — the process that PROVES shipped
    /// content compiles) minus the tester's two host assemblies (<c>MeshWeaver.PluginTester</c>,
    /// <c>MeshWeaver.Hosting.Monolith</c>). Content that references anything outside this set
    /// cannot pass the gate, so nothing outside it can be part of shipped content's compile
    /// surface. <c>FrameworkBuildIdentityTest.CanonicalList_MatchesTheTesterClosure</c> recomputes
    /// the closure from the csproj graph and fails naming the drift when the tester's references
    /// change without this list following.
    ///
    /// <para>🚨 <b>…AND EVERY OTHER MANIFEST HOST MUST RECORD THE SAME SET</b> (#1814). The list is
    /// derived from the bake host, but the identity is only useful if the CONSUMERS resolve the same
    /// value — and a canonical assembly a host does not COMPILE against gets no manifest line there
    /// and hashes as <see cref="AbsentMarker"/>, forking that host's identity while its binaries stay
    /// byte-identical. Moving <c>MeshWeaver.Import</c> and its private closure — eight names — out of
    /// the portals' compile graphs into the runtime module lane did exactly that on 2026-08-17: the
    /// bake published to an address no pod opened, every publication-side check passed, and the first
    /// pod of each deploy recompiled 269 types while instance hubs latched fault cards.
    /// <c>FrameworkBuildIdentityTest.CanonicalContentSurface_IsRecordedByEverySurfaceManifestHost</c>
    /// pins the other side of that equality, and CD compares the two resolved VALUES before
    /// publishing (<c>mw-plugin-test framework-identity … --expect …</c>, built on
    /// <see cref="ResolveIdentityForDirectory"/>). Fix a failure by giving the host its compile
    /// reference back — never by shrinking this list, which would under-invalidate.</para>
    ///
    /// <para>🚨 <b>MOVING THE BAKE/GATE CLI INTO A NEW PROJECT SILENTLY CHANGES THE FRAMEWORK
    /// IDENTITY.</b> This list is anchored to <c>tools/MeshWeaver.PluginTester</c>'s reference
    /// closure, and the identity is the hash over these assemblies' surface-manifest pairs — an
    /// assembly the PRODUCING process does not reference contributes no pair and hashes as
    /// <see cref="AbsentMarker"/>. So a tidy-up that splits <c>mw-compiler</c> (the <c>compile</c>
    /// verb, #1763) out of the tester into its own csproj with a leaner reference list resolves a
    /// DIFFERENT identity, and every bundle it bakes is then declined by every portal:
    /// <c>PrebuiltAssemblySeeder.DeclineReason</c> is doing its job, the pods simply compile
    /// everything as though no bake existed, and nothing anywhere reports a defect. The split is
    /// perfectly doable — it just has to keep this closure intact and extend
    /// <c>CanonicalList_MatchesTheTesterClosure</c> to assert BOTH projects' closures, so the
    /// equality is CHECKED rather than assumed. Keeping the verb inside the tester project is what
    /// makes the identity provably unchanged today.</para>
    /// </summary>
    public static readonly ImmutableArray<string> ContentSurfaceAssemblies =
    [
        // MeshWeaver.AI left the content surface with #2276: the engine is a MODULE now, landed
        // from the registry and composed into content compiles per-mesh via
        // CompileReferences.ComposeWithModules — never part of the framework identity. Content
        // referencing AI types compiles wherever the AI module is installed (the bake host lands
        // it too), and an image shipping AI.dll in its app closure would 409 the registry module.
        "MeshWeaver.Compiler",
        "MeshWeaver.ContentCollections",
        // MeshWeaver.Maps left the content surface with them: MapControl is the Maps MODULE now,
        // pre-installed so every portal still lands it, and the one gated tree that binds it
        // (Cornerstone) moved to the plugins repo as a Store package that REQUIRES Maps — the
        // "in-mesh content travels with the module" rule, applied.
        // MeshWeaver.ContentCollections.Indexing and .Indexing.Graph left the content surface
        // with the indexing carve-out, for the same reason MeshWeaver.Markdown.Collaboration did
        // below: the chunking/embedding pipeline is the Indexing MODULE now, composed into
        // content compiles per-mesh via CompileReferences.ComposeWithModules — never part of the
        // framework identity. No in-mesh content binds it (verified across every gated sample
        // tree); DocumentPaths, the one type the platform still needs, kept its namespace and
        // moved to MeshWeaver.Mesh.Contract, which both remaining consumers already reference.
        "MeshWeaver.Data",
        "MeshWeaver.Data.Contract",
        "MeshWeaver.Domain",
        "MeshWeaver.GitSync",
        "MeshWeaver.Graph",
        "MeshWeaver.Hosting",
        "MeshWeaver.Kernel",
        "MeshWeaver.Kernel.Hub",
        "MeshWeaver.Layout",
        "MeshWeaver.Markdown",
        // MeshWeaver.Markdown.Collaboration left the content surface with the collaboration
        // carve-out, for the same reason MeshWeaver.AI did above: comments and the tracked-change
        // view model are the MeshWeaver.Markdown.Collaboration MODULE now, composed into content compiles
        // per-mesh via CompileReferences.ComposeWithModules — never part of the framework identity.
        // In-mesh content that binds ChangeRendering / CommentNodeType (the Collaboration package's
        // own Review sources) compiles wherever that module is installed, and it is required by
        // the pre-installed Essentials package so every portal lands it.
        "MeshWeaver.Mesh.Contract",
        "MeshWeaver.Mesh.Operations",
        "MeshWeaver.Messaging.Contract",
        "MeshWeaver.Messaging.Hub",
        "MeshWeaver.NuGet",
        "MeshWeaver.Plugin.Packaging",
        "MeshWeaver.PluginCatalog",
        "MeshWeaver.Reflection",
        "MeshWeaver.ServiceProvider",
        "MeshWeaver.ShortGuid",
        "MeshWeaver.Utils",
    ];

    /// <summary>
    /// 🚨 Assemblies whose FULL implementation MVID joins the surface hash instead of their
    /// reference-assembly hash, because their CODE contributes to the GENERATED INPUT of a
    /// NodeType compile — a body-only change there alters what Roslyn is fed without any API
    /// change, so the surface hash alone would under-invalidate. Each entry names why:
    /// <list type="bullet">
    /// <item><description><c>MeshWeaver.Compiler</c> — THE compile toolchain (#1707): the
    /// skeleton generator (<c>DynamicMeshNodeAttributeGenerator</c> — the generated
    /// attribute/provider source injected into every dynamic NodeType compilation), source-query
    /// resolution (<c>CodeQueryResolver</c> — which source files a compile consumes), the
    /// <c>@@</c>-include resolution and rebasing, source aggregation/filtering/join order,
    /// parse/compilation options, generator execution, and the emit itself. Before #1707 this
    /// code lived in <c>MeshWeaver.Graph</c> and pinned ALL of Graph — the highest-churn assembly
    /// in the repo — so nearly every merge rebaked the world; the extraction is what makes
    /// "rebuild only when we need to" hold in practice. Swept 2026-08-16: the other generator in
    /// the repo (<c>SkeletonGenerator</c> in MeshWeaver.Plugin.Build) is plugin BUILD tooling —
    /// not loaded by the portal's compile path and not in the canonical set.</description></item>
    /// <item><description><c>MeshWeaver.NuGet</c> — <c>NuGetDirectiveParser</c> shapes what
    /// Roslyn is fed (which <c>#r "nuget:"</c> lines are stripped and what they resolve to), and
    /// the resolver decides which assemblies a directive adds to the reference set. A body-only
    /// change to either alters compile inputs with no API change — before #1707 this was a HOLE:
    /// the assembly was surface-hashed only.</description></item>
    /// <item><description><b>…and each root's MeshWeaver DEPENDENCY CLOSURE</b> (maintainer,
    /// 2026-08-17: "must track dependencies of compiler itself — if any have changed, need to
    /// recompile"): the toolchain CALLS into what it links (Mesh.Contract's data types,
    /// ContentCollections' config shapes, the NuGet resolver), so a body-only change in a closure
    /// member can change what the toolchain emits without any API change. The set is COMPUTED
    /// from the roots' AssemblyRef metadata — fixed bytes of the shipped assemblies, so every
    /// host derives the identical set — rather than hand-listed, so a new toolchain dependency
    /// can never be silently outside the identity. Under deterministic builds this still keeps
    /// "rebuild only when we need to": a platform update that provably touches none of the
    /// closure members' bytes moves nothing.</description></item>
    /// </list>
    /// </summary>
    public static ImmutableArray<string> FullMvidAssemblies => _fullMvidAssemblies.Value;

    /// <summary>The toolchain roots the full-MVID closure is computed from.</summary>
    internal static readonly ImmutableArray<string> ToolchainRoots =
        ["MeshWeaver.Compiler", "MeshWeaver.NuGet"];

    private static readonly Lazy<ImmutableArray<string>> _fullMvidAssemblies = new(() =>
        ComputeToolchainClosure(ToolchainRoots, ReferencedMeshWeaverAssembliesOf));

    /// <summary>
    /// The MeshWeaver-only transitive closure of <paramref name="roots"/> over
    /// <paramref name="referencedOf"/> — pure and injectable so the closure rule is unit-testable
    /// against a staged reference graph. Sorted ordinal; roots always included.
    /// </summary>
    internal static ImmutableArray<string> ComputeToolchainClosure(
        IEnumerable<string> roots,
        Func<string, IEnumerable<string>> referencedOf)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(roots);
        while (stack.Count > 0)
        {
            var name = stack.Pop();
            if (!seen.Add(name))
                continue;
            foreach (var reference in referencedOf(name))
                if (reference.StartsWith("MeshWeaver.", StringComparison.Ordinal))
                    stack.Push(reference);
        }
        return [.. seen];
    }

    /// <summary>
    /// An assembly's MeshWeaver.* AssemblyRef simple names: from the loaded assembly when
    /// present, else a metadata-only read of the DLL beside the entry assembly, else empty
    /// (the name still joins the closure via its root/parent; its MVID then resolves
    /// <see cref="AbsentMarker"/>, which is itself identity-relevant).
    /// </summary>
    private static IEnumerable<string> ReferencedMeshWeaverAssembliesOf(string simpleName)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => !a.IsDynamic
                && string.Equals(a.GetName().Name, simpleName, StringComparison.Ordinal));
        if (loaded is not null)
            return loaded.GetReferencedAssemblies()
                .Select(n => n.Name)
                .Where(n => !string.IsNullOrEmpty(n))!;

        var candidate = Path.Combine(AppContext.BaseDirectory, simpleName + ".dll");
        if (!File.Exists(candidate))
            return [];
        try
        {
            using var stream = File.OpenRead(candidate);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            var md = pe.GetMetadataReader();
            return md.AssemblyReferences
                .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Marker recorded for a canonical assembly absent from the manifest/process — the
    /// absence itself is part of the identity (two hosts with different presence sets must never
    /// share one).</summary>
    public const string AbsentMarker = "absent";

    /// <summary>
    /// The pure surface-identity computation — unit-testable without controlling how this test
    /// host was built. Hashes, ordinal-sorted over <see cref="ContentSurfaceAssemblies"/>, one
    /// <c>name=id</c> line per entry where <c>id</c> is the implementation MVID for
    /// <see cref="FullMvidAssemblies"/> members, the manifest's reference-assembly hash otherwise,
    /// and <see cref="AbsentMarker"/> when neither resolves.
    /// </summary>
    /// <param name="surfaceByName">Manifest pairs: assembly simple name → reference-assembly
    /// hash.</param>
    /// <param name="implMvidOf">Resolves an assembly's implementation MVID ("N" format), or null
    /// when the assembly is not present in this process.</param>
    public static string ComputeSurfaceIdentity(
        IReadOnlyDictionary<string, string> surfaceByName,
        Func<string, string?> implMvidOf)
        => ComputeSurfaceIdentity(surfaceByName, implMvidOf, FullMvidAssemblies);

    /// <summary>
    /// <see cref="ComputeSurfaceIdentity(IReadOnlyDictionary{string,string},Func{string,string?})"/>
    /// with the full-MVID membership supplied explicitly — the form a resolution for a FOREIGN
    /// host uses, where the toolchain closure has to be computed from THAT host's binaries rather
    /// than from this process's (see <see cref="ResolveIdentityForDirectory"/>).
    /// </summary>
    public static string ComputeSurfaceIdentity(
        IReadOnlyDictionary<string, string> surfaceByName,
        Func<string, string?> implMvidOf,
        IReadOnlyCollection<string> fullMvidAssemblies)
    {
        var text = new StringBuilder();
        foreach (var name in ContentSurfaceAssemblies)
        {
            string id;
            if (fullMvidAssemblies.Contains(name))
                id = implMvidOf(name)
                     ?? (surfaceByName.TryGetValue(name, out var surface) ? surface : AbsentMarker);
            else
                id = surfaceByName.TryGetValue(name, out var s) ? s : AbsentMarker;
            text.Append(name).Append('=').Append(id).Append('\n');
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return "s" + Convert.ToHexStringLower(hash)[..32];
    }

    /// <summary>
    /// Parses a surface manifest's text (<c>name=hash</c> lines; blank lines and malformed lines
    /// are ignored — an unreadable line degrades that assembly to <see cref="AbsentMarker"/> or
    /// the MVID fallback rather than faulting identity resolution).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseSurfaceManifest(string manifestText)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in manifestText.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0 || eq == line.Length - 1)
                continue;
            pairs[line[..eq]] = line[(eq + 1)..];
        }
        return pairs;
    }

    /// <summary>
    /// The pure resolution rule for the FALLBACK layer (no surface manifest): a non-blank stamped
    /// commit identity wins; otherwise the caller's content identity (the anchor assembly's MVID)
    /// applies.
    /// </summary>
    /// <param name="stamped">The <see cref="MetadataKey"/> attribute value, or null when the
    /// assembly carries none (every local build).</param>
    /// <param name="contentIdentity">The fallback content identity (the anchor's MVID, "N"
    /// format).</param>
    public static string Resolve(string? stamped, string contentIdentity) =>
        string.IsNullOrWhiteSpace(stamped) ? contentIdentity : stamped;

    /// <summary>
    /// The live MeshWeaver framework identity a compiled NodeType release is pinned to — THE one
    /// process-lifetime resolution, anchored on this (the MeshWeaver.Compiler) assembly. Every
    /// consumer — the assembly-store filename tag, <c>CompiledFrameworkVersion</c> stamps, the CI
    /// bake artifact key, the seeder's adoption gate, the build-protocol fingerprint — reads it
    /// from here (directly or through the delegating shims in <c>MeshWeaver.Graph</c>), so a
    /// producer and a consumer can never disagree about what "the framework" is. A mismatch
    /// against a NodeType's <c>CompiledFrameworkVersion</c> means "recompile".
    /// </summary>
    public static string FrameworkVersion => Resolved.Value.Identity;

    /// <summary>Degradation warning from the identity resolution (a torn/unusable surface
    /// manifest fell back to the stamp/MVID layer), or null on the happy path — cached with the
    /// identity itself so the pre-warmer can log it beside the identity it announces.</summary>
    public static string? FrameworkVersionWarning => Resolved.Value.Warning;

    private static readonly Lazy<(string Identity, string? Warning)> Resolved = new(() =>
        ResolveProcessIdentityWithDiagnostics(
            AppContext.BaseDirectory,
            typeof(FrameworkBuildIdentity).Assembly));

    /// <summary>
    /// The process's surface-manifest pairs (assembly simple name → reference-assembly hash),
    /// parsed once from <see cref="SurfaceManifestFileName"/> beside the app — EMPTY for
    /// manifest-less hosts, and empty (never a throw) when the file is torn: the per-type
    /// dependency record degrades to MVID ids there, exactly as the identity itself degrades to
    /// its fallback layer.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ProcessSurfacePairs => SurfacePairs.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> SurfacePairs = new(() =>
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, SurfaceManifestFileName);
            return File.Exists(path)
                ? ParseSurfaceManifest(File.ReadAllText(path))
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    });

    /// <summary>
    /// An assembly's implementation MVID by simple name in THIS process (loaded assembly, else a
    /// metadata-only read of the DLL beside the entry assembly, else null) — the same resolution
    /// the identity uses, exposed for the per-type dependency record
    /// (<see cref="CompiledDependencies"/>).
    /// </summary>
    public static string? ProcessImplMvidOf(string simpleName) =>
        ImplMvidOf(simpleName, typeof(FrameworkBuildIdentity).Assembly);

    /// <summary>
    /// Reads the stamped <see cref="MetadataKey"/> value off a LOADED assembly, or null when the
    /// assembly carries none. (For reading the same stamp off an assembly FILE without loading it,
    /// see <c>MeshWeaver.Plugin.Build.FrameworkIdentity.ReadIdentity</c>.) Since the surface
    /// identity landed this is the PROVENANCE reading — log it, never key on it when a manifest
    /// is present.
    /// </summary>
    public static string? StampedIdentityOf(Assembly assembly) =>
        assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, MetadataKey, StringComparison.Ordinal))
            ?.Value is { Length: > 0 } value
            ? value
            : null;

    /// <summary>
    /// The full resolution the process identity uses, in order: surface manifest beside the app
    /// (<c>s&lt;hash&gt;</c>) → stamped commit identity (<c>g&lt;sha&gt;</c>) → MVID-set identity.
    /// See <see cref="ResolveProcessIdentityWithDiagnostics"/> — this is its identity-only
    /// convenience reading.
    /// </summary>
    /// <param name="baseDirectory">Where to look for <see cref="SurfaceManifestFileName"/> —
    /// the app base directory in production.</param>
    /// <param name="anchorAssembly">The MeshWeaver.Compiler assembly (identity anchor — carries
    /// the commit stamp and roots the by-simple-name MVID resolution).</param>
    public static string ResolveProcessIdentity(string baseDirectory, Assembly anchorAssembly)
        => ResolveProcessIdentityWithDiagnostics(baseDirectory, anchorAssembly).Identity;

    /// <summary>
    /// The full resolution chain as one PURE, testable seam, returning the identity plus the
    /// degradation warning when the surface-manifest layer could not be used (unreadable/torn
    /// manifest, no usable pairs) — 🚨 never a throw: this runs on the boot path (the process
    /// identity Lazy), and a torn file must cost a conservative fallback identity, never the
    /// process. The caller that caches the identity for the process lifetime caches the warning
    /// with it (see <c>NodeTypeCompilationHelpers</c>); the pre-warmer logs it beside the
    /// identity it announces.
    /// </summary>
    /// <param name="baseDirectory">Where to look for <see cref="SurfaceManifestFileName"/> —
    /// the app base directory in production.</param>
    /// <param name="anchorAssembly">The MeshWeaver.Compiler assembly (identity anchor — carries
    /// the commit stamp and roots the by-simple-name MVID resolution).</param>
    public static (string Identity, string? Warning) ResolveProcessIdentityWithDiagnostics(
        string baseDirectory, Assembly anchorAssembly)
    {
        var manifestPath = Path.Combine(baseDirectory, SurfaceManifestFileName);
        string? warning = null;
        try
        {
            if (File.Exists(manifestPath))
            {
                var pairs = ParseSurfaceManifest(File.ReadAllText(manifestPath));
                if (pairs.Count > 0)
                    return (ComputeSurfaceIdentity(pairs, name => ImplMvidOf(name, anchorAssembly)), null);
                warning =
                    $"surface manifest at {manifestPath} held no usable pairs — "
                    + "resolved the stamp/MVID fallback identity instead";
            }
        }
        catch (Exception ex)
        {
            warning =
                $"surface manifest at {manifestPath} could not be read "
                + $"({ex.GetType().Name}: {ex.Message}) — resolved the stamp/MVID fallback "
                + "identity instead";
        }
        return (Resolve(
            StampedIdentityOf(anchorAssembly),
            anchorAssembly.ManifestModule.ModuleVersionId.ToString("N")), warning);
    }

    /// <summary>
    /// An assembly's implementation MVID by simple name: the anchor itself, else the loaded
    /// assembly when present, else a metadata-only read of the DLL beside the entry assembly,
    /// else null.
    /// </summary>
    private static string? ImplMvidOf(string simpleName, Assembly anchorAssembly)
    {
        if (string.Equals(anchorAssembly.GetName().Name, simpleName, StringComparison.Ordinal))
            return anchorAssembly.ManifestModule.ModuleVersionId.ToString("N");
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.Ordinal)
                                 && !a.IsDynamic);
        if (loaded is not null)
            return loaded.ManifestModule.ModuleVersionId.ToString("N");
        var candidate = Path.Combine(AppContext.BaseDirectory, simpleName + ".dll");
        if (!File.Exists(candidate))
            return null;
        try
        {
            using var stream = File.OpenRead(candidate);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            var md = pe.GetMetadataReader();
            return md.GetGuid(md.GetModuleDefinition().Mvid).ToString("N");
        }
        catch
        {
            return null;
        }
    }

    // ── Resolving ANOTHER host's identity, from its binaries alone ─────────────────────────────

    /// <summary>
    /// 🚨 The identity a host whose application binaries live in <paramref name="appDirectory"/>
    /// resolves — the SAME computation <see cref="ResolveProcessIdentityWithDiagnostics"/> performs
    /// for its own base directory, expressed over a DIRECTORY so one process can answer it for
    /// another's shipped binaries without loading a single assembly.
    ///
    /// <para>This exists because the identity is an ADDRESS: a bake publishes its bundles under the
    /// identity its own host resolves, and a portal only ever looks under the identity IT resolves.
    /// Nothing in the publication path could previously observe that the two disagree, which is
    /// exactly how issue #1814 shipped — CD baked under <c>sda0843ab…</c> while both prod pods asked
    /// for <c>s944d7fd…</c>, the bundles sat intact on the volume under an address nobody read, the
    /// bake job reported success, and the first pod of every deploy recompiled 269 types
    /// (10 m 29 s, +1598 MB) as though no bake existed. With this, CI can compare the two VALUES and
    /// fail red — see the "the identity the bake publishes must be the identity the shipped portal
    /// resolves" step in <c>main-cd.yml</c>.</para>
    ///
    /// <para>🚨 The comparison is only meaningful for the SAME ARCHITECTURE. The manifest records
    /// reference-assembly hashes of a specific build, and the same four reference assemblies differ
    /// between the amd64 and arm64 legs of one multi-arch image (see Directory.Build.props), so a
    /// multi-arch image carries two identities. Callers must extract both directories for one
    /// platform; the CI guard pins <c>--platform linux/amd64</c> out loud for that reason.</para>
    ///
    /// <para><b>Fails loudly rather than degrading.</b> A directory with no usable surface manifest
    /// yields a null identity plus a diagnostic, never a fallback value: two manifest-less
    /// directories built from the same commit would resolve the SAME fallback and a guard comparing
    /// them would report a match having verified nothing.</para>
    /// </summary>
    /// <param name="appDirectory">The host's application directory — the one holding
    /// <see cref="SurfaceManifestFileName"/> beside its assemblies (a container's <c>/app</c>).</param>
    /// <returns>The resolved surface identity, or null with <c>Problem</c> naming why not.</returns>
    public static (string? Identity, string? Problem) ResolveIdentityForDirectory(string appDirectory)
    {
        if (!Directory.Exists(appDirectory))
            return (null, $"'{appDirectory}' does not exist or is not a directory");

        var manifestPath = Path.Combine(appDirectory, SurfaceManifestFileName);
        string manifestText;
        try
        {
            if (!File.Exists(manifestPath))
                return (null,
                    $"'{manifestPath}' is missing — this host ships no surface manifest and "
                    + "therefore resolves the stamp/MVID FALLBACK identity, which no bake may be "
                    + "published under");
            manifestText = File.ReadAllText(manifestPath);
        }
        catch (Exception ex)
        {
            return (null, $"'{manifestPath}' could not be read ({ex.GetType().Name}: {ex.Message})");
        }

        var pairs = ParseSurfaceManifest(manifestText);
        if (pairs.Count == 0)
            return (null, $"'{manifestPath}' holds no usable '<name>=<hash>' pairs");

        var fullMvid = ComputeToolchainClosure(
            ToolchainRoots, name => ReferencedMeshWeaverAssembliesInDirectory(appDirectory, name));
        return (
            ComputeSurfaceIdentity(pairs, name => ImplMvidInDirectory(appDirectory, name), fullMvid),
            null);
    }

    /// <summary>
    /// The canonical assemblies a directory's surface manifest does NOT record — the exact quantity
    /// that made two hosts of one commit resolve different identities in #1814, since an unrecorded
    /// canonical assembly hashes as <see cref="AbsentMarker"/>. Reported by name so a failing guard
    /// says WHICH references a host lost rather than only that two hashes differ.
    /// </summary>
    public static ImmutableArray<string> CanonicalAssembliesAbsentFrom(
        IReadOnlyDictionary<string, string> surfaceByName) =>
        [.. ContentSurfaceAssemblies.Where(n => !surfaceByName.ContainsKey(n))];

    /// <summary>An assembly's MeshWeaver.* AssemblyRef simple names read from the DLL in
    /// <paramref name="directory"/> — metadata only, nothing loaded, so it answers for a FOREIGN
    /// host's binaries. Missing/unreadable resolves empty, exactly as the process-level walk does
    /// (the name still joins the closure; its MVID then resolves <see cref="AbsentMarker"/>).</summary>
    private static IEnumerable<string> ReferencedMeshWeaverAssembliesInDirectory(
        string directory, string simpleName)
    {
        var candidate = Path.Combine(directory, simpleName + ".dll");
        if (!File.Exists(candidate))
            return [];
        try
        {
            using var stream = File.OpenRead(candidate);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            var md = pe.GetMetadataReader();
            return md.AssemblyReferences
                .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>An assembly's implementation MVID read from the DLL in
    /// <paramref name="directory"/> — metadata only, nothing loaded.</summary>
    private static string? ImplMvidInDirectory(string directory, string simpleName)
    {
        var candidate = Path.Combine(directory, simpleName + ".dll");
        if (!File.Exists(candidate))
            return null;
        try
        {
            using var stream = File.OpenRead(candidate);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
            var md = pe.GetMetadataReader();
            return md.GetGuid(md.GetModuleDefinition().Mvid).ToString("N");
        }
        catch
        {
            return null;
        }
    }
}
