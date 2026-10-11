using System.Collections.Immutable;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// MeshWeaver#6192 - an unmet dependency floor holds the DEPENDENT back at its previous generation and the rest of
/// the landing wave is proposed. Only what cannot be held back (no previous generation on the volume, or a previous
/// version that breaks another installed package's requirement) still refuses the wave.
/// </summary>
public static class ModuleSetHoldBack
{
    /// <summary>Plans the generations to propose for <paramref name="landed"/>.</summary>
    /// <param name="installed">The install records whose requirements the set must meet.</param>
    /// <param name="landed">The activation list the set is derived from.</param>
    /// <param name="generationExists">Whether a generation directory leaf is on the volume.</param>
    public static ModuleSetHoldBackPlan Plan(
        IReadOnlyList<PackageManifest> installed, ModuleActivationList landed, Func<string, bool> generationExists)
    {
        var generations = ModuleSetStore.GenerationsOf(landed);
        var unmet = ModuleDependencyFloor.Unmet(installed, landed);
        if (unmet.IsEmpty)
            return new ModuleSetHoldBackPlan(
                generations, ImmutableList<HeldBackModule>.Empty, ImmutableList<UnmetDependencyFloor>.Empty);
        var held = new Dictionary<string, HeldBackModule>(StringComparer.OrdinalIgnoreCase);
        var heldPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refused = ImmutableList.CreateBuilder<UnmetDependencyFloor>();
        foreach (var byDependent in unmet.GroupBy(u => u.Dependent, StringComparer.OrdinalIgnoreCase))
        {
            var entry = EntryOf(byDependent.Key, installed, landed);
            if (entry is null || string.IsNullOrWhiteSpace(entry.PreviousDirectory)
                || !generationExists(entry.PreviousDirectory!))
            {
                refused.AddRange(byDependent);
                continue;
            }
            held[entry.Name] = new HeldBackModule(entry.Name, entry.PreviousDirectory!, entry.Directory!,
                entry.PreviousVersion, entry.Version, byDependent.ToImmutableList());
            heldPackages.Add(byDependent.Key);
        }
        // Holding a module back must not move the violation: a package requiring the held module at a floor its
        // PREVIOUS version does not meet keeps the wave unproposable.
        foreach (var package in installed.Where(p => !string.IsNullOrWhiteSpace(p.Id) && !heldPackages.Contains(p.Id)))
            foreach (var requirement in package.Requires)
            {
                var dependencyId = PackageDependencyGraph.DependencyId(requirement);
                if (dependencyId.Length == 0
                    || EntryOf(dependencyId, installed, landed) is not { } dependency
                    || !held.ContainsKey(dependency.Name)
                    || string.IsNullOrWhiteSpace(dependency.PreviousVersion))
                    continue;
                if (ModuleDependencyFloor.Satisfies(
                        PackageRequirement.RangeOf(requirement), dependency.PreviousVersion!) == false)
                    refused.Add(new UnmetDependencyFloor(package.Id, package.ReleasedVersion, requirement.Trim(),
                        dependencyId, dependency.Name, dependency.PreviousVersion!));
            }
        var proposed = generations;
        foreach (var module in held.Values)
            proposed = proposed.SetItem(module.Module, module.HeldGeneration);
        return new ModuleSetHoldBackPlan(
            proposed,
            held.Values.OrderBy(h => h.Module, StringComparer.OrdinalIgnoreCase).ToImmutableList(),
            refused.ToImmutable());
    }

    private static ModuleActivationEntry? EntryOf(
        string packageId, IReadOnlyList<PackageManifest> installed, ModuleActivationList landed)
    {
        static bool Landed(ModuleActivationEntry e) => e.Enabled && !string.IsNullOrWhiteSpace(e.Directory);

        var module = installed.FirstOrDefault(p =>
            string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase))?.Module;
        if (!string.IsNullOrWhiteSpace(module)
            && landed.Entries.FirstOrDefault(e =>
                string.Equals(e.Name, module, StringComparison.OrdinalIgnoreCase)) is { } byModule
            && Landed(byModule))
            return byModule;
        return landed.Entries.FirstOrDefault(e =>
            Landed(e)
            && e.PackagePath?.Split('/') is { Length: 2 } segments
            && string.Equals(segments[1], packageId, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The result of <see cref="ModuleSetHoldBack.Plan"/>.</summary>
/// <param name="Generations">The module-to-generation map to propose, held-back modules at their previous generation.</param>
/// <param name="Held">The modules held back, with the floors that caused it.</param>
/// <param name="Refused">The unmet floors holding back could not resolve; when not empty the wave is refused.</param>
public sealed record ModuleSetHoldBackPlan(
    ImmutableSortedDictionary<string, string> Generations,
    ImmutableList<HeldBackModule> Held,
    ImmutableList<UnmetDependencyFloor> Refused);

/// <summary>One module a wave's proposal withheld at its previous generation (MeshWeaver#6192).</summary>
/// <param name="Module">The held-back module (entry-assembly name).</param>
/// <param name="HeldGeneration">The previous generation the set keeps for it.</param>
/// <param name="WithheldGeneration">The landed generation the set does not take yet.</param>
/// <param name="HeldVersion">The version of the held generation, or null when unrecorded.</param>
/// <param name="WithheldVersion">The version of the withheld generation, or null when unrecorded.</param>
/// <param name="Unmet">The declared floors the withheld generation's package does not meet.</param>
public sealed record HeldBackModule(
    string Module,
    string HeldGeneration,
    string WithheldGeneration,
    string? HeldVersion,
    string? WithheldVersion,
    ImmutableList<UnmetDependencyFloor> Unmet)
{
    /// <summary>The one sentence every surface prints for it.</summary>
    public string Describe() =>
        $"'{Module}' stays at {HeldVersion ?? "(unrecorded)"} ({HeldGeneration}); "
        + $"{WithheldVersion ?? "(unrecorded)"} ({WithheldGeneration}) is withheld until its floor is met - "
        + string.Join(" | ", Unmet.Select(u => u.Describe()));
}
