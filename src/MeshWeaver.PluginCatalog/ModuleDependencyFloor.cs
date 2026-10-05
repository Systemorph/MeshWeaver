using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 THE dependency-floor check over a MODULE SET (MeshWeaver#6067): every installed package's
/// declared <see cref="PackageManifest.Requires"/> (<c>AI@^1.20.0</c>) against the version of the
/// dependency's module that the set would actually load.
///
/// <para><b>Why a set needs it.</b> A package's requirement used to be read for ORDERING only
/// (<see cref="PackageDependencyGraph"/>: "the version constraint is NOT resolved"), so nothing
/// anywhere compared it with what loads. Measured 2026-10-04 on the control instance: Hosting
/// requires <c>AI@^1.20.0</c> and its in-mesh sources call an AI 1.20 method, while the AI
/// module that loaded was the 1.19.4 build — every PR review in the fleet then failed with
/// <c>MissingMethodException</c>, and no surface had said the set was inconsistent.</para>
///
/// <para><b>Where it is enforced — the proposal, and only there.</b> A landing wave does not move
/// what the mesh runs; its PROPOSAL does (<c>Doc/Architecture/ModuleSetConvergence</c>). So the
/// check runs when a wave proposes: a set in which a requirement is not met is NOT proposed, the
/// refusal names both packages and both versions, and the mesh stays on the set it runs until a
/// wave lands a dependency that satisfies the floor. Checking at boot instead would have to read
/// install records before the mesh exists; checking per landing would judge a half-landed wave.</para>
///
/// <para><b>What it deliberately does not judge.</b> A requirement whose dependency has no landed
/// module entry here (a content-only package, a module the image ships, a package not installed) is
/// not this check's question — there is no landed version to compare. A range shape it does not
/// parse is reported as unverifiable by <see cref="Satisfies"/> returning null, never as met and
/// never as a refusal. The shapes in use across the fleet are all caret ranges (measured: 115 of
/// 115 <c>requires</c> entries in MeshWeaver.Plugins); <c>~</c>, <c>&gt;=</c>, <c>&gt;</c> and an
/// exact version are understood too.</para>
/// </summary>
public static class ModuleDependencyFloor
{
    /// <summary>How long the install-record read may take before the proposal it gates is refused
    /// (and retried by the next wave). The same budget the catalog's own record reads use.</summary>
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether <paramref name="version"/> satisfies <paramref name="range"/>: true / false, or
    /// null when the range is not a shape this understands (unverifiable, never a verdict). An
    /// empty range — a bare <c>AI</c> requirement — is met by any version. Pure.
    /// </summary>
    /// <param name="range">The constraint half of a requirement (<c>^1.20.0</c>), or empty.</param>
    /// <param name="version">The co-loaded dependency's version.</param>
    public static bool? Satisfies(string? range, string version)
        => PackageRequirement.Satisfies(range, version);

    /// <summary>
    /// Every requirement of <paramref name="installed"/> that the set <paramref name="landed"/>
    /// describes does NOT meet: the dependency's module has a landed, enabled entry here whose
    /// version is outside the declared range. Pure; ordered by dependent, then dependency.
    /// </summary>
    /// <param name="installed">The install records (each package's id, module and requirements).</param>
    /// <param name="landed">The activation list the set is derived from.</param>
    public static ImmutableList<UnmetDependencyFloor> Unmet(
        IReadOnlyList<PackageManifest> installed, ModuleActivationList landed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(landed);
        var unmet = ImmutableList.CreateBuilder<UnmetDependencyFloor>();
        foreach (var dependent in installed.Where(p => !string.IsNullOrWhiteSpace(p.Id))
                     .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var requirement in dependent.Requires)
            {
                var dependencyId = PackageDependencyGraph.DependencyId(requirement);
                if (dependencyId.Length == 0)
                    continue;
                if (LandedModuleOf(dependencyId, installed, landed) is not { } entry)
                    continue;
                var range = PackageRequirement.RangeOf(requirement);
                if (Satisfies(range, entry.Version!) == false)
                    unmet.Add(new UnmetDependencyFloor(
                        dependent.Id, dependent.ReleasedVersion, requirement.Trim(),
                        dependencyId, entry.Name, entry.Version!));
            }
        }
        return unmet.ToImmutable();
    }

    /// <summary>
    /// The landed, enabled, versioned activation entry of <paramref name="dependencyId"/>'s module:
    /// through its install record's declared <see cref="PackageManifest.Module"/> first, then
    /// through the entry's own <see cref="ModuleActivationEntry.PackagePath"/>
    /// (<c>&lt;source&gt;/&lt;package&gt;</c>). Null when this deployment lands none.
    /// </summary>
    private static ModuleActivationEntry? LandedModuleOf(
        string dependencyId, IReadOnlyList<PackageManifest> installed, ModuleActivationList landed)
    {
        static bool Usable(ModuleActivationEntry e) => e.Enabled && !string.IsNullOrWhiteSpace(e.Version);

        var module = installed.FirstOrDefault(p =>
            string.Equals(p.Id, dependencyId, StringComparison.OrdinalIgnoreCase))?.Module;
        if (!string.IsNullOrWhiteSpace(module)
            && landed.Entries.FirstOrDefault(e =>
                string.Equals(e.Name, module, StringComparison.OrdinalIgnoreCase)) is { } byModule
            && Usable(byModule))
            return byModule;
        return landed.Entries.FirstOrDefault(e =>
            Usable(e)
            && e.PackagePath?.Split('/') is { Length: 2 } segments
            && string.Equals(segments[1], dependencyId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// This deployment's install records — the requirements the proposal is checked against — read
    /// as the system (a record is not a user's to hide from the check), bounded by
    /// <see cref="ReadBudget"/>. A failed or late read FAULTS, and every proposing caller already
    /// answers a fault by staying on the current set: a set must never be proposed unchecked.
    /// </summary>
    /// <param name="hub">The mesh hub.</param>
    public static IObservable<IReadOnlyList<PackageManifest>> ReadInstalled(IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var mesh = hub.ServiceProvider.GetService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        if (mesh is null)
            return Observable.Return<IReadOnlyList<PackageManifest>>([]);
        IObservable<QueryResultChange<MeshNode>> Query() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{PackageInstaller.InstalledPartition} scope:children nodeType:{PackageInstaller.PackageNodeType}"))
            .Take(1);
        return access.RunAsSystem(Query)
            .Timeout(ReadBudget)
            .Select(change => (IReadOnlyList<PackageManifest>)change.Items
                .Select(n => n.ContentAs<PackageManifest>(hub.JsonSerializerOptions))
                .OfType<PackageManifest>()
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .ToImmutableList());
    }

    /// <summary>
    /// Ends a landing wave the one checked way: reads the install records, then proposes the set
    /// the activation record describes only when every requirement is met
    /// (<see cref="ModuleLandingService.ProposeCheckedModuleSet"/>).
    /// Faults when the set is refused or the records cannot be read — the callers' existing answer
    /// to a fault is "the mesh stays on its current set, the next wave proposes again".
    /// </summary>
    /// <param name="hub">The mesh hub.</param>
    /// <param name="landing">The landing service.</param>
    public static IObservable<ModuleSet?> ProposeChecked(IMessageHub hub, ModuleLandingService landing)
    {
        ArgumentNullException.ThrowIfNull(landing);
        return ReadInstalled(hub).SelectMany(landing.ProposeCheckedModuleSet);
    }
}

/// <summary>
/// One requirement a module set does not meet (MeshWeaver#6067): <paramref name="Dependent"/>
/// declares <paramref name="Requirement"/>, and the set loads <paramref name="Module"/> at
/// <paramref name="LoadedVersion"/>, outside it.
/// </summary>
/// <param name="Dependent">The package that declares the requirement.</param>
/// <param name="DependentVersion">Its installed released version, or null when unrecorded.</param>
/// <param name="Requirement">The requirement as declared (<c>AI@^1.20.0</c>).</param>
/// <param name="Dependency">The required package's id.</param>
/// <param name="Module">The dependency's module (entry-assembly name).</param>
/// <param name="LoadedVersion">The version of that module the set would load.</param>
public sealed record UnmetDependencyFloor(
    string Dependent, string? DependentVersion, string Requirement,
    string Dependency, string Module, string LoadedVersion)
{
    /// <summary>The one sentence every surface prints for it — both packages, both versions.</summary>
    public string Describe() =>
        $"'{Dependent}'{(DependentVersion is { Length: > 0 } v ? $" {v}" : "")} requires {Requirement}, "
        + $"but this set loads '{Module}' (package '{Dependency}') at {LoadedVersion}, which does "
        + "not satisfy it";
}
