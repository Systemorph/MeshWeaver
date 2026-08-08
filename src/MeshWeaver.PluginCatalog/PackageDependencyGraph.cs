using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The package dependency graph — the ONE place that reads
/// <see cref="PackageManifest.Requires"/> and turns it into an install order.
///
/// <para>Dependencies are declared on the package root's own content (a node-repo's
/// <c>index.json</c> → <c>content.requires</c>, or a <c>package.json</c> manifest) as entries
/// shaped <c>Store@^1.0.0</c>. The version constraint is NOT resolved: a registry serves exactly
/// one version of a package, so ordering is the only thing the constraint can influence today.
/// Everything here reads the id half and ignores the rest.</para>
///
/// <para>🚨 Ordering is not cosmetic — installing out of order FAILS. The installer refuses an
/// instance whose NodeType is not yet present ("NodeType(s) not registered: Training/Tour"), which
/// is exactly what happened on the first live unattended run when catalog (alphabetical) order put
/// <c>Chess</c> before <c>Training</c>. A human clicking Install picks an order implicitly; every
/// automated caller has to derive it.</para>
///
/// <para>Two callers with deliberately DIFFERENT cycle policies, which is why the graph exposes
/// both a tolerant sort and an explicit cycle report:</para>
/// <list type="bullet">
///   <item><b>The unattended boot pass</b> (<see cref="InstanceAutoRegistrationService"/>) uses
///     <see cref="InDependencyOrder"/>: a cycle is a repo authoring error nobody is present to fix,
///     so it warns and still emits every package exactly once. Refusing to boot over it would
///     strand the whole instance for one malformed package.</item>
///   <item><b>A person clicking Install</b> (<see cref="CatalogLayoutAreas.InstallPackage"/>) uses
///     <see cref="InstallClosure"/>, which throws <see cref="FindCycle"/>'s named cycle. There IS
///     someone to tell, and silently installing a cycle in arbitrary order would fail later with a
///     NodeType path that names neither package.</item>
/// </list>
/// </summary>
public static class PackageDependencyGraph
{
    /// <summary>
    /// The package id half of a requirement entry: <c>"Store@^1.0.0"</c> → <c>"Store"</c>. Returns
    /// an empty string for a blank/version-only entry, which every caller treats as "no dependency".
    /// </summary>
    /// <param name="requirement">A <see cref="PackageManifest.Requires"/> entry.</param>
    /// <returns>The dependency's package id, trimmed.</returns>
    public static string DependencyId(string? requirement) =>
        string.IsNullOrWhiteSpace(requirement) ? "" : requirement.Split('@')[0].Trim();

    /// <summary>
    /// Orders <paramref name="packages"/> so a dependency comes BEFORE anything that declares it —
    /// a depth-first topological sort that is TOLERANT by design: a dependency outside
    /// <paramref name="packages"/> is ignored (the instance was not granted it, so there is nothing
    /// to order against), and a cycle is warned about rather than thrown.
    /// </summary>
    ///
    /// <remarks>
    /// 🚨 What a cycle does, precisely — it is NOT "falls back to catalog order", which this used to
    /// claim and never did. The DFS drops the single BACK EDGE that closes the loop and keeps going,
    /// so the guarantee is:
    /// <list type="bullet">
    ///   <item>every package is emitted exactly once (never dropped, never duplicated, never
    ///     infinite);</item>
    ///   <item>every dependency edge that is still satisfiable is respected — including edges from
    ///     inside the cycle to packages outside it;</item>
    ///   <item>the relative order of the packages WITHIN a cycle is unspecified (for
    ///     <c>A→B, B→A</c> the result is <c>[B, A]</c>, i.e. NOT catalog order).</item>
    /// </list>
    /// Keeping the DFS result is deliberate rather than a limitation: inside a cycle no order is
    /// correct, and a stable "catalog order" fallback would be strictly worse — it would also
    /// discard the satisfiable edges from the cycle's members to everything else.
    /// <c>PluginDependencyOrderTest.ACycle_DropsOnlyTheBackEdge_AndKeepsEveryOtherConstraint</c>
    /// pins that guarantee.
    /// </remarks>
    /// <param name="packages">The packages to order, in catalog order.</param>
    /// <param name="logger">Receives the cycle warning, if any.</param>
    /// <returns>The same packages, every one exactly once, dependencies first.</returns>
    public static IReadOnlyList<PackageManifest> InDependencyOrder(
        IReadOnlyList<PackageManifest> packages, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var byId = packages
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var ordered = new List<PackageManifest>(packages.Count);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 1 = visiting, 2 = done

        void Visit(PackageManifest pkg)
        {
            if (state.TryGetValue(pkg.Id, out var s))
            {
                if (s == 1)
                    logger?.LogWarning(
                        "Dependency cycle involving package '{Id}' — ignoring the requirement that "
                        + "closes the loop; order within the cycle is arbitrary.",
                        pkg.Id);
                return;
            }
            state[pkg.Id] = 1;
            foreach (var requirement in pkg.Requires)
            {
                var depId = DependencyId(requirement);
                if (depId.Length > 0 && byId.TryGetValue(depId, out var dep) && !ReferenceEquals(dep, pkg))
                    Visit(dep);
            }
            state[pkg.Id] = 2;
            ordered.Add(pkg);
        }

        foreach (var pkg in packages)
            Visit(pkg);
        return ordered;
    }

    /// <summary>
    /// Names a dependency cycle among <paramref name="packages"/>, or <c>null</c> when the graph is
    /// acyclic. The message walks the cycle so it can be read without opening the repo — e.g.
    /// <c>"A → B → A"</c>. Only dependencies present in <paramref name="packages"/> participate:
    /// an id nobody supplies cannot close a cycle.
    /// </summary>
    /// <param name="packages">The packages to inspect.</param>
    /// <returns>The cycle as <c>"A → B → A"</c>, or <c>null</c>.</returns>
    public static string? FindCycle(IReadOnlyList<PackageManifest> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var byId = packages
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();

        string? Visit(PackageManifest pkg)
        {
            if (state.TryGetValue(pkg.Id, out var s))
            {
                if (s != 1) return null;
                // Re-entering a node still on the stack: the cycle is the stack from its first
                // occurrence, closed back onto itself.
                var start = path.IndexOf(pkg.Id);
                return string.Join(" → ", path.Skip(start).Append(pkg.Id));
            }
            state[pkg.Id] = 1;
            path.Add(pkg.Id);
            foreach (var requirement in pkg.Requires)
            {
                var depId = DependencyId(requirement);
                if (depId.Length == 0 || !byId.TryGetValue(depId, out var dep) || ReferenceEquals(dep, pkg))
                    continue;
                var cycle = Visit(dep);
                if (cycle is not null) return cycle;
            }
            path.RemoveAt(path.Count - 1);
            state[pkg.Id] = 2;
            return null;
        }

        foreach (var pkg in packages)
        {
            var cycle = Visit(pkg);
            if (cycle is not null) return cycle;
        }
        return null;
    }

    /// <summary>
    /// What one Install click must actually install: <paramref name="target"/> preceded by every
    /// transitive dependency the instance does not already have — in dependency order, so nothing is
    /// ever imported before the types it references exist.
    ///
    /// <para>Already-installed dependencies are SKIPPED, not re-installed or updated: an install
    /// record means the package's nodes are present and its NodeTypes were released, and quietly
    /// upgrading a package the user did not name would be a surprise. Bringing a stale dependency
    /// forward stays the Update button's job.</para>
    ///
    /// <para>A dependency the catalog does not offer is logged and stepped over rather than
    /// refused — the same tolerance the boot pass applies. The instance may simply not be granted
    /// it, the package may still install fine, and if it genuinely cannot the installer's own
    /// "NodeType(s) not registered" refusal is the accurate error.</para>
    /// </summary>
    /// <param name="target">The package the user clicked Install on.</param>
    /// <param name="catalog">Every package the source offers (the resolution universe).</param>
    /// <param name="installedIds">Ids already present in the install registry.</param>
    /// <param name="logger">Receives the un-offered-dependency warnings.</param>
    /// <returns><paramref name="target"/> last, preceded by the dependencies to install first.</returns>
    /// <exception cref="InvalidOperationException">The dependency graph reachable from
    /// <paramref name="target"/> contains a cycle — named in the message.</exception>
    public static IReadOnlyList<PackageManifest> InstallClosure(
        PackageManifest target,
        IReadOnlyList<PackageManifest> catalog,
        IReadOnlySet<string> installedIds,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(installedIds);

        var byId = catalog
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        // The target itself may be absent from the catalog list a caller passes (a test, a
        // single-package source); it is always part of its own closure.
        byId[target.Id] = target;

        // Reachable set first, so the cycle report names only packages this click would touch —
        // an unrelated cycle elsewhere in the catalog must not block installing this package.
        var reachable = new List<PackageManifest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<PackageManifest>();
        queue.Enqueue(target);
        seen.Add(target.Id);
        while (queue.Count > 0)
        {
            var pkg = queue.Dequeue();
            reachable.Add(pkg);
            foreach (var requirement in pkg.Requires)
            {
                var depId = DependencyId(requirement);
                if (depId.Length == 0 || !seen.Add(depId))
                    continue;
                if (byId.TryGetValue(depId, out var dep))
                    queue.Enqueue(dep);
                else
                    logger?.LogWarning(
                        "Package '{Id}' requires '{Dependency}', which this catalog does not offer — "
                        + "installing without it.", pkg.Id, depId);
            }
        }

        var cycle = FindCycle(reachable);
        if (cycle is not null)
            throw new InvalidOperationException(
                $"Cannot install '{target.Id}': its dependencies form a cycle ({cycle}). "
                + "One of the packages in that loop must drop the requirement.");

        return InDependencyOrder(reachable, logger)
            // The target always installs, even on a re-install/update click; only its DEPENDENCIES
            // are skipped for being present.
            .Where(p => string.Equals(p.Id, target.Id, StringComparison.Ordinal)
                        || !installedIds.Contains(p.Id))
            .ToImmutableList();
    }
}
