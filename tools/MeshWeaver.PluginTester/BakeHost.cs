using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The PLATFORM HOST a bake compiles against and is ADDRESSED to — its framework identity, its
/// surface-manifest pairs, its implementation MVIDs and its metadata-reference set, all resolved
/// from ONE application directory so none of the four can name a different host than the others.
///
/// <para><b>Why this exists (#3022).</b> The bake used to take every one of those from the process
/// it ran in — the <c>mw-plugin-test</c> image's own <c>/app</c> — while the bundles it produced
/// are adopted by the PORTAL image, whose <c>/app</c> is a strict superset: measured on
/// <c>3.0.0-rc9.ci.7534</c>, the portal ships 219 assemblies and the tester 88, and 21
/// <c>MeshWeaver.*</c> assemblies exist only in the portal (<c>MeshWeaver.Maps</c>,
/// <c>MeshWeaver.AI</c>, <c>MeshWeaver.ContentCollections.Indexing</c>, the Blazor and hosting
/// halves…). Content that binds one of them compiles in every portal and fails the bake with a
/// CS0234/CS0246 that names the CONTENT — which is how <c>Cornerstone/Pricing</c> and the map
/// galleries held the whole release wave (no seal, no dependents, no adoption) the day
/// <c>MeshWeaver.Maps</c> left the tester's closure. The doctrine was already written
/// (ModuleBuildArchitecture: <i>"the platform image is the compiler and the reference set"</i>);
/// the module lane followed it since #2907; the bake did not.</para>
///
/// <para><b>The rule.</b> <c>--app &lt;dir&gt;</c> makes the platform host's directory the reference
/// set (its <c>/app</c> plus its IMPLEMENTATION shared frameworks, never this process's TPA), the
/// identity the bundles are keyed to (<see cref="FrameworkBuildIdentity.ResolveIdentityForDirectory"/>
/// — the host's own address, by construction), and the environment the per-type dependency
/// records are computed against (the host's manifest pairs and MVIDs — what its
/// <c>PrebuiltAssemblySeeder</c> validates on adopt). Without <c>--app</c> the host is this
/// process, exactly as before.</para>
///
/// <para>🚨 <b>One invariant makes recording the host's identity honest: the TOOLCHAIN that ran
/// must be the host's own bytes.</b> The identity folds the implementation MVIDs of
/// <see cref="FrameworkBuildIdentity.FullMvidAssemblies"/> in because their CODE shapes the
/// compile input (skeleton, source-query resolution, emit). That code executes in THIS process,
/// from this process's <c>MeshWeaver.Compiler.dll</c> — so if it is not byte-for-byte the host's,
/// the bake would claim a toolchain it did not run. The check is member-by-member over the
/// closure computed from the host's binaries, and a mismatch is a refusal naming both MVIDs,
/// never a note. (Measured on rc9.ci.7534: all 25 assemblies the two images share are
/// byte-identical — the check costs nothing on a same-wave pair and catches a mixed one.)</para>
/// </summary>
internal sealed class BakeHost
{
    /// <summary>The identity every bundle of this bake is keyed to — the HOST's.</summary>
    public required string FrameworkIdentity { get; init; }

    /// <summary>The application directory whose assemblies are the reference set.</summary>
    public required string AppDirectory { get; init; }

    /// <summary>The surface-id resolver for the per-type dependency records — the host's
    /// manifest pairs, the composed modules' MVIDs, the host's implementation MVIDs.</summary>
    public required Func<string, string?> IdOf { get; init; }

    /// <summary>The toolchain id for the records' reserved entry, over the host's MVIDs.</summary>
    public required string ToolchainId { get; init; }

    /// <summary>The metadata references every NodeType compiles against.</summary>
    public required IReadOnlyList<MetadataReference> References { get; init; }

    /// <summary>One line for the log naming what the reference set IS.</summary>
    public required string Description { get; init; }

    /// <summary>True when the host is this process's own application directory.</summary>
    public required bool IsThisProcess { get; init; }

    /// <summary>A degradation or divergence worth printing beside the identity, or null.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// The host every bake resolved before <c>--app</c> existed: this process — its
    /// <c>TRUSTED_PLATFORM_ASSEMBLIES</c> composed with the modules, its manifest, its MVIDs, its
    /// live identity. Correct exactly when the process IS the platform (the portal compiling its
    /// own content at runtime; the platform's own image baking its own Doc tree).
    /// </summary>
    /// <param name="modules">The composed module assemblies (see <see cref="TreeBake.LoadExternalModules"/>).</param>
    public static BakeHost InProcess(IReadOnlyList<InstalledModuleAssembly> modules) =>
        new()
        {
            FrameworkIdentity = PrebuiltAssemblySeeder.LiveFrameworkMvid,
            AppDirectory = AppContext.BaseDirectory,
            IdOf = CompiledDependencies.CreateIdResolver(
                FrameworkBuildIdentity.ProcessSurfacePairs,
                TreeBake.ModuleMvidsOf(modules),
                FrameworkBuildIdentity.ProcessImplMvidOf),
            ToolchainId = CompiledDependencies.ComputeToolchainId(FrameworkBuildIdentity.ProcessImplMvidOf),
            References = CompileReferences.ComposeWithModules(modules),
            Description =
                $"reference set = this process's own application directory '{AppContext.BaseDirectory}' "
                + $"(TRUSTED_PLATFORM_ASSEMBLIES) + {modules.Count} composed module(s)",
            IsThisProcess = true,
            Note = PrebuiltAssemblySeeder.LiveFrameworkIdentityWarning is { } warning
                ? $"framework identity degraded — {warning}"
                : null,
        };

    /// <summary>
    /// Resolves ANOTHER host as the bake's platform: <paramref name="appDirectory"/> is that host's
    /// application directory (a portal image's <c>/app</c>, extracted or mounted) and
    /// <paramref name="sharedFrameworksRoot"/> its <c>&lt;dotnet root&gt;/shared</c>. Returns the
    /// problem instead of a host — never a fallback to this process — when the directory has no
    /// resolvable identity, is not a readable reference set, or ships a toolchain this process is
    /// not running.
    /// </summary>
    /// <param name="appDirectory">The platform host's application directory.</param>
    /// <param name="sharedFrameworksRoot">That host's shared-frameworks root — REQUIRED, because the
    /// running runtime's is the right answer only when this process runs inside the host's image,
    /// and "which one applies" must never be inferred (a console runtime lacks the ASP.NET Core
    /// framework the portal compiles against).</param>
    /// <param name="modules">The composed module assemblies.</param>
    public static (BakeHost? Host, string? Problem) ResolveDirectory(
        string appDirectory,
        string sharedFrameworksRoot,
        IReadOnlyList<InstalledModuleAssembly> modules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedFrameworksRoot);
        var app = Path.GetFullPath(appDirectory);

        // 🚨 The ADDRESS first. A directory with no usable surface manifest resolves the stamp/MVID
        // fallback identity, which no bake may be published under (two manifest-less hosts of one
        // commit resolve the SAME fallback, so a comparison over them verifies nothing). Refuse.
        var (identity, identityProblem) = FrameworkBuildIdentity.ResolveIdentityForDirectory(app);
        if (identity is null)
            return (null,
                $"the platform host at '{app}' resolves no framework identity — {identityProblem}. "
                + "A bake is ADDRESSED to the host it compiles against, and a host without a surface "
                + "manifest is not one any portal resolves; nothing baked against it could be adopted. "
                + "Pass the platform image's /app (the directory holding "
                + $"{FrameworkBuildIdentity.SurfaceManifestFileName} beside its assemblies).");

        // 🚨 THE TOOLCHAIN INVARIANT — see the type remarks. Member by member, both sides named.
        var drift = FrameworkBuildIdentity.ToolchainClosureOf(app)
            .Select(name => (
                Name: name,
                Running: FrameworkBuildIdentity.ProcessImplMvidOf(name),
                Host: FrameworkBuildIdentity.ImplMvidInDirectory(app, name)))
            .Where(x => !string.Equals(x.Running, x.Host, StringComparison.Ordinal))
            .ToImmutableArray();
        if (drift.Length > 0)
            return (null,
                $"the compile toolchain this process runs is not the one the platform host at '{app}' "
                + "ships, so a bake keyed to that host's identity would claim a toolchain it did not "
                + "run. Differing (running vs host): "
                + string.Join("; ", drift.Select(d =>
                    $"{d.Name} mvid {d.Running ?? CompiledDependencies.AbsentId} vs {d.Host ?? CompiledDependencies.AbsentId}"))
                + ". Run this CLI from the tester image of the SAME CD wave as the platform image "
                + "(the two pins move together), or run it from a host composed of the platform's own "
                + "/app (compose-gate-host.sh).");

        ContainerReferenceSet set;
        try
        {
            // 🚨 NOT this process's TPA. The reference set is the HOST's /app plus the HOST's
            // implementation shared frameworks — what the portal's own runtime compile sees —
            // and nothing this process happens to carry (its own hosting assemblies, its CLI).
            set = ContainerReferenceSet.Read(
                app, trustedPlatformAssemblies: string.Empty, sharedFrameworksRoot: sharedFrameworksRoot);
        }
        catch (ContainerReferenceSet.UnreadableContainerException ex)
        {
            return (null,
                $"the platform host at '{app}' is not a reference set this bake can compile against — "
                + ex.Message);
        }

        var references = new List<MetadataReference>(set.AssembliesByName.Count + modules.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var fromApp = 0;
        var fromFrameworks = 0;
        foreach (var path in set.AssemblyPaths)
        {
            if (!IsManagedAssembly(path))
            {
                skipped++;
                continue;
            }
            if (!seen.Add(path))
                continue;
            references.Add(MetadataReference.CreateFromFile(path));
            if (path.StartsWith(app, StringComparison.Ordinal))
                fromApp++;
            else
                fromFrameworks++;
        }
        foreach (var module in modules)
        {
            var location = module.Assembly.Location;
            if (!string.IsNullOrEmpty(location) && File.Exists(location) && seen.Add(location))
                references.Add(MetadataReference.CreateFromFile(location));
        }

        var pairs = FrameworkBuildIdentity.ParseSurfaceManifest(
            File.ReadAllText(Path.Combine(app, FrameworkBuildIdentity.SurfaceManifestFileName)));
        string? HostMvidOf(string name) => FrameworkBuildIdentity.ImplMvidInDirectory(app, name);
        var live = PrebuiltAssemblySeeder.LiveFrameworkMvid;
        return (new BakeHost
        {
            FrameworkIdentity = identity,
            AppDirectory = app,
            IdOf = CompiledDependencies.CreateIdResolver(pairs, TreeBake.ModuleMvidsOf(modules), HostMvidOf),
            ToolchainId = CompiledDependencies.ComputeToolchainId(HostMvidOf),
            References = references,
            Description =
                $"reference set = platform host '{app}' ({fromApp} assemblies) + its shared frameworks "
                + $"under '{Path.GetFullPath(sharedFrameworksRoot)}' ({fromFrameworks} assemblies) + "
                + $"{modules.Count} composed module(s)"
                + (skipped == 0 ? string.Empty : $"; {skipped} non-managed file(s) skipped"),
            IsThisProcess = false,
            // Informational, not a refusal: with the toolchain verified equal, a process whose OTHER
            // canonical surfaces differ from the host's still emits bytes bound to the host's
            // assemblies (they are the references) and records the host's ids. The bake is valid
            // for the host — and only for the host, which the identity now says.
            Note = string.Equals(live, identity, StringComparison.Ordinal)
                ? null
                : $"this process resolves '{live}' but the bake is keyed to the host's '{identity}' — "
                  + "the toolchain is verified identical, so the bundles are valid for that host and "
                  + "for no other",
        }, null);
    }

    /// <summary>A file the compiler can take as a metadata reference — a PE with CLI metadata.
    /// Native libraries occasionally carry the <c>.dll</c> extension and would fault Roslyn later,
    /// on a path that names neither the file nor the reason.</summary>
    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            return pe.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
