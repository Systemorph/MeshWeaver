using System.Collections.Immutable;
using MeshWeaver.Mesh;
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
/// target's <see cref="ReleaseArtifacts.PlatformSurface"/>. A package with no landed module has
/// nothing that would keep running across the roll; a package whose module the target's sealed
/// set already carries is measured too (cheap, and the verdict is a useful diagnostic) but the
/// rule does not read it — the published build is what will be adopted. When the observation
/// carries NO surface (a publication that predates #3651, or a document that did not parse) nothing
/// is measured and the rule reports every landed module as Indeterminate for the link check —
/// which is neither clearance nor a hold.</para>
///
/// <para>Cost: one metadata read per landed module — the type references of one assembly resolved
/// against an in-memory dictionary — on the caller's IO pool. Never a load, never a type touched.</para>
/// </summary>
public static class ModuleLinkObservation
{
    /// <summary>
    /// Links every landed module among <paramref name="packages"/> against
    /// <paramref name="artifacts"/>' platform surface and returns the observation with the verdicts
    /// filled in. Total: a module whose bytes cannot be read answers
    /// <see cref="ModuleLinkState.Indeterminate"/> with the reason, never a throw. Reads files —
    /// run it where IO is allowed.
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
        foreach (var package in packages)
        {
            if (string.IsNullOrWhiteSpace(package.ModuleName)
                || string.IsNullOrWhiteSpace(package.LandedModulePath))
                continue;
            var verdict = ModulePlatformLink.Check(package.LandedModulePath, surface);
            links[package.Name] = verdict;
            if (verdict.State != ModuleLinkState.Linkable)
                logger?.LogInformation(
                    "[ReleaseGate] {Package}: landed module {Module} against the published surface "
                    + "of {Identity}: {Report}",
                    package.Name, package.ModuleName, surface.Identity ?? "(unnamed identity)",
                    verdict.Report());
        }
        return artifacts with { ModuleLinks = links.ToImmutable() };
    }
}
