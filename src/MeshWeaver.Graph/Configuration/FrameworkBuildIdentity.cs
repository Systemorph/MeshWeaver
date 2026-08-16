using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// THE framework build identity every compiled NodeType release is pinned to (issue #1660 WS3) —
/// the value <see cref="NodeTypeCompilationHelpers.FrameworkVersion"/> resolves and every other
/// reading (the assembly-store filename tag, <c>CompiledFrameworkVersion</c> stamps, the CI bake
/// artifact key, <c>PrebuiltAssemblySeeder</c>'s adoption gate, the build-protocol fingerprint)
/// flows from. One identity, one resolution — a producer and a consumer can never disagree about
/// what "the framework" is.
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
/// <item><description><b>The Graph MVID</b> — local builds with neither: a content identity,
/// exact for a dirty working tree.</description></item>
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
    /// </summary>
    public static readonly ImmutableArray<string> ContentSurfaceAssemblies =
    [
        "MeshWeaver.AI",
        "MeshWeaver.Application.Styles",
        "MeshWeaver.Approvals",
        "MeshWeaver.ContentCollections",
        "MeshWeaver.ContentCollections.Indexing",
        "MeshWeaver.ContentCollections.Indexing.Graph",
        "MeshWeaver.Data",
        "MeshWeaver.Data.Contract",
        "MeshWeaver.DataSetReader",
        "MeshWeaver.DataSetReader.Csv",
        "MeshWeaver.DataSetReader.Excel",
        "MeshWeaver.DataSetReader.Excel.BinaryFormat",
        "MeshWeaver.DataSetReader.Excel.OpenXmlFormat",
        "MeshWeaver.DataSetReader.Excel.Utils",
        "MeshWeaver.DataStructures",
        "MeshWeaver.Domain",
        "MeshWeaver.GitSync",
        "MeshWeaver.Graph",
        "MeshWeaver.Hosting",
        "MeshWeaver.Import",
        "MeshWeaver.Kernel",
        "MeshWeaver.Kernel.Hub",
        "MeshWeaver.Layout",
        "MeshWeaver.Maps",
        "MeshWeaver.Markdown",
        "MeshWeaver.Markdown.Collaboration",
        "MeshWeaver.Mesh.Contract",
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
    /// <item><description><c>MeshWeaver.Graph</c> — carries the NodeType compile pipeline's
    /// emitters and source shaping: <c>DynamicMeshNodeAttributeGenerator</c> (the generated
    /// attribute/skeleton source injected into every dynamic NodeType compilation),
    /// <c>MeshNodeCompilationService</c> (source aggregation, <c>@@</c> include resolution and
    /// rebasing, <c>#r</c>/NuGet directive handling), and <c>CodeQueryResolver</c> (which source
    /// files a compile consumes). Swept 2026-08-16: the other generator in the repo
    /// (<c>SkeletonGenerator</c> in MeshWeaver.Plugin.Build) is plugin BUILD tooling — not loaded
    /// by the portal's compile path and not in the canonical set.</description></item>
    /// </list>
    /// </summary>
    public static readonly ImmutableArray<string> FullMvidAssemblies = ["MeshWeaver.Graph"];

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
    {
        var text = new StringBuilder();
        foreach (var name in ContentSurfaceAssemblies)
        {
            string id;
            if (FullMvidAssemblies.Contains(name))
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
    /// commit identity wins; otherwise the caller's content identity (the Graph MVID) applies.
    /// </summary>
    /// <param name="stamped">The <see cref="MetadataKey"/> attribute value, or null when the
    /// assembly carries none (every local build).</param>
    /// <param name="contentIdentity">The fallback content identity (Graph's MVID, "N" format).</param>
    public static string Resolve(string? stamped, string contentIdentity) =>
        string.IsNullOrWhiteSpace(stamped) ? contentIdentity : stamped;

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
    /// (<c>s&lt;hash&gt;</c>) → stamped commit identity (<c>g&lt;sha&gt;</c>) → Graph MVID.
    /// See <see cref="ResolveProcessIdentityWithDiagnostics"/> — this is its identity-only
    /// convenience reading.
    /// </summary>
    /// <param name="baseDirectory">Where to look for <see cref="SurfaceManifestFileName"/> —
    /// the app base directory in production.</param>
    /// <param name="graphAssembly">The MeshWeaver.Graph assembly (identity anchor).</param>
    public static string ResolveProcessIdentity(string baseDirectory, Assembly graphAssembly)
        => ResolveProcessIdentityWithDiagnostics(baseDirectory, graphAssembly).Identity;

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
    /// <param name="graphAssembly">The MeshWeaver.Graph assembly (identity anchor).</param>
    public static (string Identity, string? Warning) ResolveProcessIdentityWithDiagnostics(
        string baseDirectory, Assembly graphAssembly)
    {
        var manifestPath = Path.Combine(baseDirectory, SurfaceManifestFileName);
        string? warning = null;
        try
        {
            if (File.Exists(manifestPath))
            {
                var pairs = ParseSurfaceManifest(File.ReadAllText(manifestPath));
                if (pairs.Count > 0)
                    return (ComputeSurfaceIdentity(pairs, name => ImplMvidOf(name, graphAssembly)), null);
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
            StampedIdentityOf(graphAssembly),
            graphAssembly.ManifestModule.ModuleVersionId.ToString("N")), warning);
    }

    /// <summary>
    /// An assembly's implementation MVID by simple name: the loaded assembly when present (Graph —
    /// the only current <see cref="FullMvidAssemblies"/> member — is always loaded, since this
    /// code IS Graph), else a metadata-only read of the DLL beside the entry assembly, else null.
    /// </summary>
    private static string? ImplMvidOf(string simpleName, Assembly graphAssembly)
    {
        if (string.Equals(graphAssembly.GetName().Name, simpleName, StringComparison.Ordinal))
            return graphAssembly.ManifestModule.ModuleVersionId.ToString("N");
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
}
