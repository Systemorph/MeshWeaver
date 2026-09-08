using System.Collections.Immutable;

namespace MeshWeaver.Compiler;

/// <summary>How a platform host ships one <c>MeshWeaver.*</c> assembly.</summary>
public enum PlatformShipping
{
    /// <summary>The file sits in the host's application directory itself — the app closure, loaded
    /// into the default ALC at start and never overridable. A second copy landing beside a module
    /// is dead weight at best: the loader keeps the one it saw first, and the loser reads as
    /// "landed but not yet loaded".</summary>
    ApplicationClosure,

    /// <summary>The host's own <c>meshweaver-surface.manifest</c> names it — the platform's written
    /// record of what it COMPILED against, which is the app closure by construction.</summary>
    SurfaceManifest,

    /// <summary>The image seeds it under <c>&lt;app&gt;/modules/&lt;Name&gt;/&lt;Name&gt;.dll</c> —
    /// the closure lane of <c>MeshModulesPublish.targets</c>, which is where
    /// <c>MeshBuilder.ResolveModulePath</c> probes FIRST. A landed bundle of that same module
    /// legitimately supersedes this copy; a bundle merely CARRYING it as a riding sibling does
    /// not, and produces two builds of one assembly name in one process.</summary>
    SeededModule,
}

/// <summary>One assembly a platform host ships, with the evidence that says so.</summary>
/// <param name="Name">The assembly's simple name.</param>
/// <param name="How">Which of the three witnesses answered.</param>
/// <param name="Evidence">The path (or manifest line) the answer was read from — printed by every
/// caller, so a strip decision names the file it rests on rather than an opinion.</param>
public sealed record PlatformShippedAssembly(string Name, PlatformShipping How, string Evidence);

/// <summary>
/// 🚨 <b>THE PLATFORM-SHIPPED WITNESS — what a platform host ACTUALLY SHIPS, measured off that
/// host's own application directory, never read from a list.</b>
///
/// <para><b>Why a list cannot do this job (MeshWeaver#3732).</b> A module bundle may carry the
/// <c>MeshWeaver.*</c> assemblies that exist nowhere in the host, and must never carry one the host
/// already has: <c>MeshWeaver.*</c> assemblies bind by a strictly synchronised
/// <c>AssemblyVersion</c>, so two copies under one simple name are ONE assembly identity and the
/// loader keeps whichever it saw first. Until now that split was decided by a hand-maintained file
/// in each node repo (<c>src/platform-shipped.txt</c>), and a declaration goes stale silently in
/// both directions the moment a project moves in or out of the image:</para>
/// <list type="bullet">
///   <item>MeshWeaver.Plugins#1023 removed <c>MeshWeaver.AI</c> from the list when the engine left
///     <c>/app</c>; MeshWeaver.Plugins#1515 put four modules back under <c>Modules:Assemblies</c>
///     and the list did not move — so 14 of 37 bundles ship a copy of an assembly the image seeds.</item>
///   <item>#3335 found FIVE names in <c>/app</c> that the list did not name, each a duplicate in
///     every bundle that referenced it, and corrected the file BY HAND after a manual
///     re-measurement — which is the same measurement this type makes automatically.</item>
///   <item>The opposite error costs a <c>ReflectionTypeLoadException</c>: a name listed as
///     image-shipped that the image no longer ships reaches a mesh from nowhere at all
///     (<c>MeshWeaver.Maps</c>, #3335).</item>
/// </list>
///
/// <para><b>Three witnesses, because the image ships assemblies three ways</b> — and reading only
/// the first is exactly how <c>MeshWeaver.Markdown.Collaboration</c> came to ride 14 bundles while
/// the image also seeds it:</para>
/// <list type="number">
///   <item><see cref="PlatformShipping.ApplicationClosure"/> — <c>&lt;app&gt;/&lt;Name&gt;.dll</c>.</item>
///   <item><see cref="PlatformShipping.SurfaceManifest"/> — a name in
///     <see cref="FrameworkBuildIdentity.SurfaceManifestFileName"/>.</item>
///   <item><see cref="PlatformShipping.SeededModule"/> —
///     <c>&lt;app&gt;/modules/&lt;Name&gt;/&lt;Name&gt;.dll</c>. 🚨 A <c>MeshModuleClosure</c> row
///     touches NEITHER of the first two (the hosts' csproj comments say so in as many words), so a
///     witness that reads only <c>/app</c> answers "not shipped" for every seeded module.</item>
/// </list>
///
/// <para><b>It refuses rather than answering nothing.</b> A directory with no surface manifest and
/// no <c>MeshWeaver.*</c> assembly at its root is not a platform application directory, and
/// answering "ships nothing" for one would make every caller's strip a no-op that reads exactly
/// like a clean measurement. Pure file reads, so it is unit-testable with no image on disk.</para>
/// </summary>
public static class PlatformShippedAssemblies
{
    /// <summary>The folder a host lays seeded modules out under, beside the app
    /// (<c>memex/MeshModulesPublish.targets</c>; <c>MeshBuilder.ResolveModulePath</c> probes it
    /// before the app directory).</summary>
    public const string SeededModulesFolder = "modules";

    /// <summary>
    /// The names that bind by a strictly synchronised <c>AssemblyVersion</c> and therefore collapse
    /// to ONE identity in a loaded process. The same predicate
    /// <c>MeshWeaver.Plugin.Build.DepsClosure</c>, <c>PrivateClosure</c> and
    /// <c>PublishedBundleCatalogue</c> apply, kept here so "what the platform side owns" has one
    /// spelling. A third-party diamond rides by design, versions independently, and is deliberately
    /// NOT judged.
    /// </summary>
    public static bool IsPlatformAssemblyName(string? name) =>
        !string.IsNullOrEmpty(name)
        && (name.StartsWith("MeshWeaver.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "MeshWeaver", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads what the platform host at <paramref name="appDirectory"/> ships.
    /// </summary>
    /// <param name="appDirectory">The host's application directory — a portal image's <c>/app</c>,
    /// extracted or mounted.</param>
    /// <returns>The shipped assemblies by simple name (ordinal-ignore-case), or <c>null</c> with a
    /// <c>Problem</c> naming why this directory cannot answer.</returns>
    public static (ImmutableDictionary<string, PlatformShippedAssembly>? Shipped, string? Problem)
        Read(string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
            return (null, "no platform application directory was given");
        var app = Path.GetFullPath(appDirectory);
        if (!Directory.Exists(app))
            return (null, $"'{app}' does not exist or is not a directory");

        var shipped = ImmutableDictionary.CreateBuilder<string, PlatformShippedAssembly>(
            StringComparer.OrdinalIgnoreCase);
        var rootAssemblies = 0;
        try
        {
            // 1. The application closure.
            foreach (var file in Directory.EnumerateFiles(app, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!IsPlatformAssemblyName(name))
                    continue;
                rootAssemblies++;
                shipped[name] = new PlatformShippedAssembly(
                    name, PlatformShipping.ApplicationClosure, file);
            }

            // 2. The host's own surface manifest — its record of what it compiled against.
            var manifestPath = Path.Combine(app, FrameworkBuildIdentity.SurfaceManifestFileName);
            var manifestPairs = 0;
            if (File.Exists(manifestPath))
            {
                var pairs = FrameworkBuildIdentity.ParseSurfaceManifest(File.ReadAllText(manifestPath));
                manifestPairs = pairs.Count;
                foreach (var name in pairs.Keys)
                {
                    if (!IsPlatformAssemblyName(name) || shipped.ContainsKey(name))
                        continue;
                    shipped[name] = new PlatformShippedAssembly(
                        name, PlatformShipping.SurfaceManifest, manifestPath);
                }
            }

            // 3. The seeded-module lane. ONLY <dir>/<dir name>.dll: that is the file
            //    ResolveModulePath probes, and the other files in a seeded folder are that module's
            //    own private closure, on no probing path of any other module.
            var modulesRoot = Path.Combine(app, SeededModulesFolder);
            if (Directory.Exists(modulesRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(modulesRoot))
                {
                    var name = Path.GetFileName(directory);
                    if (!IsPlatformAssemblyName(name) || shipped.ContainsKey(name))
                        continue;
                    var dll = Path.Combine(directory, name + ".dll");
                    if (File.Exists(dll))
                        shipped[name] = new PlatformShippedAssembly(
                            name, PlatformShipping.SeededModule, dll);
                }
            }

            // 🚨 A witness that read nothing must say so. "This host ships no MeshWeaver assembly"
            // is not a fact any real platform directory produces — a portal /app carries 200+ and
            // the tester's 88 — so an empty reading means the caller pointed at the wrong place,
            // and returning it as an answer would make every strip a silent no-op that logs like a
            // clean measurement.
            if (rootAssemblies == 0 && manifestPairs == 0)
                return (null,
                    $"'{app}' carries no {FrameworkBuildIdentity.SurfaceManifestFileName} and no "
                    + "MeshWeaver.* assembly at its root, so it is not a platform application "
                    + "directory. Pass a portal image's extracted /app (the directory holding the "
                    + "surface manifest beside its assemblies) — an empty reading here would strip "
                    + "nothing while looking exactly like a clean measurement.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"'{app}' could not be read ({ex.GetType().Name}: {ex.Message})");
        }

        return (shipped.ToImmutable(), null);
    }
}
