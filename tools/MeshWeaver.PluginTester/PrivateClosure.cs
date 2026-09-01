using System.Collections.Immutable;

namespace MeshWeaver.PluginTester;

/// <summary>
/// A container-built module's PRIVATE runtime closure: the non-framework assemblies its own
/// <c>PackageReference</c>s pull in, which therefore have to RIDE the bundle.
///
/// <para><b>Why this exists — MeshWeaver.Plugins#1043, the second fleet-wide closure-accounting
/// outage.</b> The container build resolves references from the platform image's <c>/app</c>, and
/// the flip to <c>"build": "container"</c> made "the image has this file" mean "the platform
/// supplies it, so the bundle need not". That reading is false, and silently so: the reference
/// image is a PORTAL, and a portal that has a module compiled into it also carries that module's
/// private package dependencies. <c>memex-portal-ai</c>'s <c>/app</c> holds
/// <c>Microsoft.Agents.AI.dll</c> only because <c>MeshWeaver.AI</c> is built into that image —
/// core references it nowhere, and the tester image (100 assemblies, same promoted wave) has no
/// copy. So the published <c>MeshWeaver.AI</c> bundle listed three assemblies and none of its own
/// third-party closure; it loaded on that one portal and threw
/// <c>ReflectionTypeLoadException: Microsoft.Agents.AI, Version=1.17.0.0</c> everywhere else —
/// the bake gate here, and MeshWeaver.Reinsurance's trunk, which merely SEEDS the published Store
/// bundle and had no way to see why.</para>
///
/// <para><b>The rule, and it is the SDK path's rule.</b> A bundle carries the transitive closure
/// of the module's own package references, MINUS the shared framework. That is exactly what
/// <c>DepsClosure</c> derives from a publish folder for <c>--deps-closure</c> — including the
/// diamond ("a package reachable from the module's own references AND from its MeshWeaver.*
/// references is bundled anyway"), because <c>Assembly.LoadFrom</c> resolves the app closure FIRST
/// and only probes the module directory for what the app does not have. So a duplicate costs
/// bytes, while an omission costs a type load. The shared framework is the one honest exemption:
/// it travels with every host that can load the module at all.</para>
///
/// <para><b>Where the bytes come from.</b> The container's <c>/app</c> first — those are the exact
/// assemblies the compile bound against — then the curated module-libraries shelf for anything the
/// image does not carry. Both are recorded sources with a deps.json behind them; nothing is
/// guessed, which is the property the lane's no-<c>--extra-refs</c> stance protects.</para>
///
/// <para>Pure data-in/data-out over the two records, so the derivation is unit-testable with no
/// container and no shelf on disk.</para>
/// </summary>
public static class PrivateClosure
{
    /// <summary>One assembly riding the bundle.</summary>
    /// <param name="AssemblyName">The assembly's simple name.</param>
    /// <param name="SourcePath">The file to copy beside the module.</param>
    /// <param name="PackageId">The package that contributed it.</param>
    /// <param name="Source">Where the bytes came from — <c>the image</c> or <c>the shelf</c>.</param>
    public sealed record Ride(string AssemblyName, string SourcePath, string PackageId, string Source);

    /// <summary>The derived closure.</summary>
    /// <param name="Rides">Every assembly that must travel with the bundle, ordered by name.</param>
    /// <param name="FrameworkResolved">Assembly names left out because a shared framework supplies
    /// them — the ONE sound omission, listed so a log can show it was a decision.</param>
    /// <param name="Missing">Assembly names the walk reached that neither the image nor the shelf
    /// has a file for. Never silently dropped: a package that compiled but cannot be materialized
    /// is the shape of a bundle that faults at first use.</param>
    public sealed record Result(
        ImmutableArray<Ride> Rides,
        ImmutableArray<string> FrameworkResolved,
        ImmutableArray<string> Missing);

    /// <summary>
    /// Derives the private closure of a set of <c>PackageReference</c> ids.
    /// </summary>
    /// <param name="packageIds">The project's declared package references.</param>
    /// <param name="container">The container reference set (its deps.json is the dependency record
    /// and its <c>/app</c> the preferred byte source).</param>
    /// <param name="shelf">The module-libraries shelf, when one is staged.</param>
    /// <returns>The closure.</returns>
    public static Result Derive(
        IEnumerable<string> packageIds, ContainerReferenceSet container, ModuleLibrariesShelf? shelf)
    {
        ArgumentNullException.ThrowIfNull(packageIds);
        ArgumentNullException.ThrowIfNull(container);

        var rides = new Dictionary<string, Ride>(StringComparer.OrdinalIgnoreCase);
        var frameworkResolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(packageIds);

        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!seen.Add(id))
                continue;
            // The platform's OWN assemblies are ProjectReferences, never rides: shipping one beside
            // the module lands a same-identity duplicate of a strictly versioned binary (#143).
            if (IsPlatform(id))
                continue;

            var knownToShelf = shelf?.Knows(id) == true;
            var names = knownToShelf ? shelf!.AssembliesOf(id) : container.AssembliesOf(id);
            foreach (var name in names)
            {
                if (IsPlatform(name))
                    continue;
                if (container.IsFrameworkSupplied(name))
                {
                    frameworkResolved.Add(name);
                    continue;
                }
                if (rides.ContainsKey(name))
                    continue;
                // The image first: those are the exact bytes the compile bound against. The shelf
                // supplies what the image does not carry — which is the definition of an
                // ADDITIONAL library.
                var fromImage = container.FindAssembly(name);
                if (fromImage is not null)
                    rides[name] = new Ride(name, fromImage, id, "the image");
                else if (shelf?.FileFor(name) is { } fromShelf)
                    rides[name] = new Ride(name, fromShelf, id, "the shelf");
                else
                    missing.Add(name);
            }

            // Both records are followed: the shelf pins additional libraries, the image pins
            // everything it carries, and a package can be known to one and not the other.
            foreach (var dependency in container.DependenciesOf(id))
                pending.Push(dependency);
            if (shelf is not null)
                foreach (var dependency in shelf.DependenciesOf(id))
                    pending.Push(dependency);
        }

        return new Result(
            [.. rides.Values.OrderBy(r => r.AssemblyName, StringComparer.OrdinalIgnoreCase)],
            [.. frameworkResolved],
            [.. missing]);
    }

    private static bool IsPlatform(string name) =>
        name.StartsWith("MeshWeaver.", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "MeshWeaver", StringComparison.OrdinalIgnoreCase);
}
