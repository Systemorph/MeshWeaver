using System.Collections.Immutable;
using System.IO.Compression;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>The IO half of the measured module gate (#3651).</b> <see cref="ReleaseAvailability"/> is
/// pure and reads nothing; this is the one place the landed generation's bytes meet the target's
/// published surface, and it writes the verdicts onto the observation as data
/// (<see cref="ReleaseArtifacts.ModuleLinks"/>) so every caller — the poller's selection, the
/// gate endpoint, a test over a temp root — hands the rule the same shape.
///
/// <para><b>What is measured, and what deliberately is not.</b> Every package that names a module
/// AND has a landed generation on this instance is linked
/// (<see cref="ModulePlatformLink.Check(string, ModulePlatformSurface)"/> — the entry DLL on disk,
/// its siblings as the module's own closure, exactly as the boot probe measures it) against the
/// target's <see cref="ReleaseArtifacts.PlatformSurface"/> PLUS the other installed modules as boot
/// loads them after the roll (<see cref="ModulePlatformSurface.WithSiblingModules"/>): the
/// target-published build of a module the target publishes, else its landed generation. A package
/// with no landed module has nothing that would keep running across the roll; a package whose module
/// the target's sealed set already carries is measured too (cheap, and the verdict is a useful
/// diagnostic) but the rule does not read it — the published build is what will be adopted. When the
/// observation carries NO surface (a publication that predates #3651, or a document that did not
/// parse) nothing is measured and the rule reports every landed module as Indeterminate for the link
/// check — which is neither clearance nor a hold.</para>
///
/// <para>Cost: one metadata read per landed module — the type references of one assembly resolved
/// against an in-memory dictionary — on the caller's IO pool, plus one extraction per target-published
/// sibling module into a scratch directory removed before returning. Never a load, never a type
/// touched.</para>
/// </summary>
public static class ModuleLinkObservation
{
    /// <summary>
    /// Links every landed module among <paramref name="packages"/> against
    /// <paramref name="artifacts"/>' platform surface plus the post-roll sibling modules, and returns
    /// the observation with the verdicts filled in. Total: a module whose bytes cannot be read — or
    /// whose sibling's cannot — answers <see cref="ModuleLinkState.Indeterminate"/> with the reason,
    /// never a throw. Reads files — run it where IO is allowed.
    /// </summary>
    /// <param name="artifacts">The observation of the target identity.</param>
    /// <param name="packages">What must survive the roll, with each module's landed path.</param>
    /// <param name="logger">Diagnostics.</param>
    public static ReleaseArtifacts Measure(
        ReleaseArtifacts artifacts, IEnumerable<RequiredPackage> packages, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(packages);
        if (artifacts.PlatformSurface is not { } surface)
            return artifacts;

        var links = artifacts.ModuleLinks.ToBuilder();
        var withModule = packages.Where(p => !string.IsNullOrWhiteSpace(p.ModuleName)).ToList();
        var scratch = Path.Combine(Path.GetTempPath(), "mw-link-siblings-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 🚨 The POST-ROLL module set, read once: each installed module as boot loads it after the
            // roll — the target-published build when the target publishes one (it is adopted at the
            // roll, ReleaseAvailability's module lane), else the landed generation. Measuring a
            // dependent against a sibling's OLD landed bytes would clear it against an API the
            // replacement may have removed.
            var postRoll = withModule
                .Select(p => (Package: p, Read: PostRollFiles(p, artifacts.Modules, surface, scratch)))
                .ToList();
            for (var i = 0; i < postRoll.Count; i++)
            {
                var package = postRoll[i].Package;
                if (string.IsNullOrWhiteSpace(package.LandedModulePath))
                    continue;
                // 🚨 Against the target's platform PLUS the OTHER modules boot loads beside this one —
                // never the image alone, which reads a reference to a sibling MODULE (MeshWeaver.AI) as
                // "no such platform assembly" and held memex.systemorph.com on 3.0.0-ci.10310.
                var self = i;
                var others = postRoll.Where((_, j) => j != self).ToList();
                var unreadable = others.FirstOrDefault(other => other.Read.Failure is not null);
                var verdict = unreadable.Read.Failure is { } failure
                    ? new ModuleLinkVerdict(ModuleLinkState.Indeterminate, package.ModuleName!, [], 0, [], [],
                        $"the module {unreadable.Package.ModuleName}, which loads beside it after the roll, could "
                        + $"not be read, so this module cannot be measured against it: {failure}")
                    : ModulePlatformLink.Check(package.LandedModulePath!,
                        surface.WithSiblingModules(others.SelectMany(other => other.Read.Files)));
                links[package.Name] = verdict;
                // Anything but a clean Linkable is worth a line — a hard verdict, an unknown, or a
                // Linkable that carries roll-forward version drift (#4083, reported and never a hold).
                if (verdict.State != ModuleLinkState.Linkable || !verdict.Advisories.IsDefaultOrEmpty)
                    logger?.LogInformation(
                        "[ReleaseGate] {Package}: landed module {Module} against the published surface "
                        + "of {Identity}: {Report}",
                        package.Name, package.ModuleName, surface.Identity ?? "(unnamed identity)",
                        verdict.Report());
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                    Directory.Delete(scratch, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(exception,
                    "[ReleaseGate] could not remove the sibling scratch directory {Path}", scratch);
            }
        }
        return artifacts with { ModuleLinks = links.ToImmutable() };
    }

    /// <summary>
    /// One installed module's assemblies as boot loads them AFTER the roll — the <c>MeshWeaver.*</c>
    /// files the platform does not bind itself: the target-published module bundle's when the target
    /// publishes this module (extracted into <paramref name="scratch"/>), else the landed generation
    /// directory's, else none. Read EAGERLY, so a generation removed or unreadable mid-measurement is a
    /// named <c>Failure</c>, never a lazy enumeration throwing later.
    /// </summary>
    private static (ImmutableArray<string> Files, string? Failure) PostRollFiles(
        RequiredPackage package, SealedModuleSet? published, ModulePlatformSurface surface, string scratch)
    {
        try
        {
            if (published?.BundlePathByModule.TryGetValue(package.ModuleName!, out var bundle) == true)
            {
                var target = Path.Combine(scratch, package.ModuleName!);
                Directory.CreateDirectory(target);
                using var archive = ZipFile.OpenRead(bundle);
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName;
                    if (!name.StartsWith(NuGetPackageWriter.ModuleFolder + "/", StringComparison.Ordinal)
                        || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                        || name[(NuGetPackageWriter.ModuleFolder.Length + 1)..].Contains('/')
                        || !IsModuleOwned(Path.GetFileNameWithoutExtension(name), surface))
                        continue;
                    entry.ExtractToFile(Path.Combine(target, Path.GetFileName(name)), overwrite: true);
                }
                return ([.. Directory.GetFiles(target, "*.dll").OrderBy(f => f, StringComparer.Ordinal)], null);
            }
            var directory = string.IsNullOrWhiteSpace(package.LandedModulePath)
                ? null
                : Path.GetDirectoryName(package.LandedModulePath);
            if (string.IsNullOrEmpty(directory))
                return ([], null);
            return ([.. Directory.GetFiles(directory, "*.dll")
                .Where(file => IsModuleOwned(Path.GetFileNameWithoutExtension(file), surface))
                .OrderBy(file => file, StringComparer.Ordinal)], null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            return ([], $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>A module's own assembly: a <c>MeshWeaver.*</c> name the platform does not bind itself
    /// (a third-party dependency, or a platform copy riding along, is not module surface).</summary>
    private static bool IsModuleOwned(string name, ModulePlatformSurface surface)
        => name.StartsWith(ModulePlatformLink.PlatformAssemblyPrefix, StringComparison.Ordinal)
           && !surface.IsPlatformBound(name);
}
