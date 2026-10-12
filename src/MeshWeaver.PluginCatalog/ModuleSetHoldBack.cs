using System.Collections.Immutable;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// MeshWeaver#6192 - an unmet dependency floor holds the DEPENDENT back and the rest of the landing wave is proposed.
/// The dependent is held at the generation the mesh's last ACCEPTED module set runs (<see cref="ModuleSetIndex.Current"/>,
/// the newest proposal a replica has booted onto): the one generation of it known to have run coherently. Never at the
/// immediately displaced landing (<see cref="ModuleActivationEntry.PreviousDirectory"/>) - after two refused waves that
/// names a generation no set ever carried. The held version's own requirements are not on record (the install record
/// states the withheld version's), so the plan refuses the wave wherever the safety of the held set cannot be established.
/// </summary>
public static class ModuleSetHoldBack
{
    /// <summary>Plans the generations to propose for <paramref name="landed"/>.</summary>
    /// <param name="installed">The install records whose requirements the set must meet.</param>
    /// <param name="landed">The activation list the set is derived from.</param>
    /// <param name="sets">The module set records, read whole; <see cref="ModuleSetIndex.Current"/> is the accepted set.</param>
    /// <param name="generationPresent">Whether a module's generation has its entry assembly on the volume.</param>
    /// <returns>The generations to propose, the modules held back, and every reason the wave is refused.</returns>
    public static ModuleSetHoldBackPlan Plan(
        IReadOnlyList<PackageManifest> installed,
        ModuleActivationList landed,
        ModuleSetIndex? sets,
        Func<string, string, bool> generationPresent)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(landed);
        ArgumentNullException.ThrowIfNull(generationPresent);
        var generations = ModuleSetStore.GenerationsOf(landed);
        var unmet = ModuleDependencyFloor.Unmet(installed, landed);
        if (unmet.IsEmpty)
            return new ModuleSetHoldBackPlan(generations, [], []);

        var accepted = sets?.Current;
        var acceptedGenerations = accepted?.Generations
            ?? ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
        var running = sets?.RunningGenerations
            ?? ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
        var refusals = ImmutableList.CreateBuilder<string>();
        var held = new Dictionary<string, HeldBackModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var byDependent in unmet.GroupBy(u => u.Dependent, StringComparer.OrdinalIgnoreCase))
        {
            var floors = byDependent.ToImmutableList();
            var why = string.Join(" | ", floors.Select(f => f.Describe()));
            if (EntryOf(byDependent.Key, installed, landed) is not { } entry)
            {
                refusals.Add($"{why} - package '{byDependent.Key}' lands no module here, so there is nothing to hold back");
                continue;
            }
            if (accepted is null)
            {
                refusals.Add($"{why} - '{entry.Name}' cannot be held back: the mesh has no accepted module set (no "
                    + "proposal has been adopted by a replica), so no generation of it is known to have run coherently");
                continue;
            }
            if (!acceptedGenerations.TryGetValue(entry.Name, out var acceptedGeneration)
                || string.IsNullOrWhiteSpace(acceptedGeneration))
            {
                refusals.Add($"{why} - '{entry.Name}' cannot be held back: the accepted module set {accepted.Sequence} "
                    + "does not name it");
                continue;
            }
            if (string.Equals(acceptedGeneration, entry.Directory, StringComparison.OrdinalIgnoreCase))
            {
                refusals.Add($"{why} - '{entry.Name}' cannot be held back: the accepted module set {accepted.Sequence} "
                    + $"already runs generation '{acceptedGeneration}'");
                continue;
            }
            if (running.TryGetValue(entry.Name, out var ran)
                && !string.Equals(ran, acceptedGeneration, StringComparison.OrdinalIgnoreCase))
            {
                refusals.Add($"{why} - '{entry.Name}' cannot be held back: the replica that adopted the accepted module "
                    + $"set {accepted.Sequence} did not run its generation '{acceptedGeneration}' (it ran '{ran}')");
                continue;
            }
            if (!generationPresent(entry.Name, acceptedGeneration))
            {
                refusals.Add($"{why} - '{entry.Name}' cannot be held back: generation '{acceptedGeneration}' of the "
                    + $"accepted module set {accepted.Sequence} is not on the volume");
                continue;
            }
            held[entry.Name] = new HeldBackModule(entry.Name, byDependent.Key, acceptedGeneration, entry.Directory!,
                VersionOf(entry, acceptedGeneration), entry.Version, accepted.Sequence, floors);
        }

        var proposed = generations;
        foreach (var module in held.Values)
            proposed = proposed.SetItem(module.Module, module.HeldGeneration);

        // A held version's own requirements are unknown, so every dependency its package declares must be proposed at
        // the very generation the accepted set ran it beside - the one combination known to have run.
        foreach (var module in held.Values)
            foreach (var requirement in installed
                         .Where(p => string.Equals(p.Id, module.Package, StringComparison.OrdinalIgnoreCase))
                         .SelectMany(p => p.Requires))
            {
                var dependencyId = PackageDependencyGraph.DependencyId(requirement);
                if (dependencyId.Length == 0 || EntryOf(dependencyId, installed, landed) is not { } dependency)
                    continue;
                proposed.TryGetValue(dependency.Name, out var proposedAt);
                acceptedGenerations.TryGetValue(dependency.Name, out var acceptedAt);
                if (!string.Equals(proposedAt, acceptedAt, StringComparison.OrdinalIgnoreCase))
                    refusals.Add($"'{module.Module}' would be held back at '{module.HeldGeneration}' of the accepted "
                        + $"module set {module.AcceptedSequence}, but its dependency '{dependency.Name}' (package "
                        + $"'{dependencyId}') would be proposed at '{proposedAt ?? "(none)"}', not at the "
                        + $"'{acceptedAt ?? "(none)"}' it was accepted beside - the held version's requirements cannot "
                        + "be established");
            }

        // Holding a module back must not move the violation onto a package that requires it.
        foreach (var package in installed.Where(p => !string.IsNullOrWhiteSpace(p.Id)))
        {
            if (held.Values.Any(h => string.Equals(h.Package, package.Id, StringComparison.OrdinalIgnoreCase)))
                continue;
            foreach (var requirement in package.Requires)
            {
                var dependencyId = PackageDependencyGraph.DependencyId(requirement);
                if (dependencyId.Length == 0
                    || EntryOf(dependencyId, installed, landed) is not { } dependency
                    || !held.TryGetValue(dependency.Name, out var heldModule))
                    continue;
                var range = PackageRequirement.RangeOf(requirement);
                if (heldModule.HeldVersion is not { Length: > 0 } heldVersion)
                {
                    if (!string.IsNullOrWhiteSpace(range))
                        refusals.Add($"'{package.Id}' requires {requirement.Trim()}, and '{heldModule.Module}' would be "
                            + $"held back at generation '{heldModule.HeldGeneration}', whose version is not recorded - the "
                            + "requirement cannot be checked against it");
                    continue;
                }
                if (ModuleDependencyFloor.Satisfies(range, heldVersion) == false)
                    refusals.Add(new UnmetDependencyFloor(package.Id, package.ReleasedVersion, requirement.Trim(),
                            dependencyId, heldModule.Module, heldVersion).Describe()
                        + $" - holding '{heldModule.Module}' back at the accepted {heldVersion} would only move the violation");
            }
        }

        if (refusals.Count > 0)
            return new ModuleSetHoldBackPlan(generations, [], refusals.ToImmutable());
        return new ModuleSetHoldBackPlan(
            proposed, held.Values.OrderBy(h => h.Module, StringComparer.OrdinalIgnoreCase).ToImmutableList(), []);
    }

    /// <summary>The version the held generation was landed at, while the activation entry still records it.</summary>
    private static string? VersionOf(ModuleActivationEntry entry, string generation) =>
        string.Equals(entry.PreviousDirectory, generation, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(entry.PreviousVersion)
            ? entry.PreviousVersion
            : null;

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
/// <param name="Generations">The module-to-generation map to propose, held-back modules at their accepted generation.</param>
/// <param name="Held">The modules held back, with the floors that caused it.</param>
/// <param name="Refusals">Why the wave cannot be proposed; when not empty nothing is proposed.</param>
public sealed record ModuleSetHoldBackPlan(
    ImmutableSortedDictionary<string, string> Generations,
    ImmutableList<HeldBackModule> Held,
    ImmutableList<string> Refusals);

/// <summary>One module a wave's proposal withheld at the generation the accepted module set runs (MeshWeaver#6192).</summary>
/// <param name="Module">The held-back module (entry-assembly name).</param>
/// <param name="Package">The package whose declared floor is unmet.</param>
/// <param name="HeldGeneration">The accepted set's generation the proposal keeps for it.</param>
/// <param name="WithheldGeneration">The landed generation the proposal does not take yet.</param>
/// <param name="HeldVersion">The version of the held generation, or null when unrecorded.</param>
/// <param name="WithheldVersion">The version of the withheld generation, or null when unrecorded.</param>
/// <param name="AcceptedSequence">The sequence of the accepted module set the held generation comes from.</param>
/// <param name="Unmet">The declared floors the withheld generation's package does not meet.</param>
public sealed record HeldBackModule(
    string Module,
    string Package,
    string HeldGeneration,
    string WithheldGeneration,
    string? HeldVersion,
    string? WithheldVersion,
    long AcceptedSequence,
    ImmutableList<UnmetDependencyFloor> Unmet)
{
    /// <summary>The one sentence every surface prints for it.</summary>
    public string Describe() =>
        $"'{Module}' (package '{Package}') stays at {HeldVersion ?? "(unrecorded)"} ({HeldGeneration}), the generation "
        + $"accepted module set {AcceptedSequence} runs; {WithheldVersion ?? "(unrecorded)"} ({WithheldGeneration}) is "
        + "withheld until its floor is met - " + string.Join(" | ", Unmet.Select(u => u.Describe()));
}
