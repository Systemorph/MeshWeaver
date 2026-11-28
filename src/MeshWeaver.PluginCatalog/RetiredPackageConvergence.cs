using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Mesh.Features;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>A package the registry PROVES retired is converged away — and nothing else is</b>
/// (MeshWeaver#8619, <c>Doc/Architecture/RetiredPackageConvergence</c>, policy
/// <c>declared-not-derived</c>).
///
/// <para>A package retired from the plugin repository leaves an instance holding its install record
/// and its partition for ever; every pod start then parked the retired types with an Error because no
/// bundle can carry a package that no longer exists. The proof of retirement is an EXPLICIT entry in
/// the registry's listing (<see cref="RegistryListing.Retired"/>), declared in the source repository
/// (<see cref="NodeRepoPackageSource.RetiredFileName"/>). A package merely ABSENT from a listing is
/// never retired — an outage, a partial listing, an ungranted source and a retired package all look
/// the same there, which is exactly why absence proves nothing.</para>
///
/// <para>What a proven retirement does, on this instance:</para>
/// <list type="number">
/// <item>It is DECLARED to the compile control plane (<see cref="IRetiredPackages"/>), which skips the
/// package's types quietly — one Information per boot, no Error, no park.</item>
/// <item>The install record is removed through phase 1 of the ONE package-uninstall request
/// (<see cref="DeclaredDisabledRetirement"/>, run as System): the module binding and the install
/// record go, the partition's data STAYS, and phase 2 can never follow (no requester). A retired
/// package another installed package requires is refused, by name, exactly as a flag retirement is.</item>
/// </list>
/// </summary>
public static class RetiredPackageConvergence
{
    /// <summary>The flag name a registry retirement is issued under — the same request machinery as a
    /// flag retirement, so its requests and logs name this origin.</summary>
    public const string Flag = "registry-retired";

    /// <summary>
    /// The declarations a listing PROVES. Pure. A retired id that the same listing also lists as a
    /// package is NOT proven (a re-introduced package: the listing contradicts itself, and the
    /// listing's presence wins — the safe direction). An entry without an id is skipped.
    /// </summary>
    /// <param name="source">The package source the listing came from.</param>
    /// <param name="listing">A SUCCESSFUL listing.</param>
    public static ImmutableList<RetiredPackageDeclaration> Proven(string source, RegistryListing listing)
    {
        var listed = listing.Packages.Select(p => p.Id).ToImmutableHashSet(StringComparer.Ordinal);
        return [.. listing.Retired
            .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !listed.Contains(r.Id))
            .Select(r => new RetiredPackageDeclaration(r.Source is { Length: > 0 } own ? own : source, r.Id, r.Reason ?? ""))
            .DistinctBy(r => (r.Source, r.PackageId))];
    }

    /// <summary>The composition the retirement is issued over: the proven packages as source-scoped
    /// retired patterns (<c>{source}/{id}</c>), so a package of another source with the same id is
    /// never matched. Pure.</summary>
    /// <param name="proven">What <see cref="Proven"/> returned.</param>
    public static FeatureComposition CompositionOf(IEnumerable<RetiredPackageDeclaration> proven)
    {
        var retired = proven
            .Select(r => new FeaturePackage(Flag, $"{r.Source}/{r.PackageId}"))
            .ToImmutableList();
        return new FeatureComposition([], retired) { Retired = retired };
    }

    /// <summary>
    /// Declares one SUCCESSFUL listing's retirements to the compile control plane, and says so once
    /// per package. Never faults.
    /// </summary>
    /// <param name="hub">The mesh hub.</param>
    /// <param name="source">The package source the listing came from.</param>
    /// <param name="listing">A successful listing.</param>
    /// <param name="logger">Receives the declaration.</param>
    /// <returns>The proven retirements.</returns>
    public static ImmutableList<RetiredPackageDeclaration> Declare(
        IMessageHub hub, string source, RegistryListing listing, ILogger logger)
    {
        var proven = Proven(source, listing);
        if (hub.ServiceProvider.GetService<IRetiredPackages>() is { } ledger)
        {
            // Replaces this source's earlier declaration: a package it stops declaring stops being retired.
            ledger.Declare(source, proven);
        }
        return proven;
    }

    /// <summary>
    /// Retires the installed packages <paramref name="proven"/> names through phase 1 of the one
    /// uninstall request (data retained). Never faults; emits once. Nothing proven ⇒ nothing is read
    /// and nothing is written.
    /// </summary>
    /// <param name="hub">The mesh hub.</param>
    /// <param name="proven">What <see cref="Proven"/> returned for the successful listings of this boot.</param>
    /// <param name="logger">Receives every decision.</param>
    public static IObservable<DeclaredDisabledRetirement.Outcome> Retire(
        IMessageHub hub, IReadOnlyCollection<RetiredPackageDeclaration> proven, ILogger logger)
    {
        if (proven.Count == 0)
            return Observable.Return(DeclaredDisabledRetirement.Outcome.Empty);
        logger.LogInformation(
            "[RegistryRetire] the registry declares {Count} package(s) retired: [{Packages}]. Their types are "
            + "skipped quietly and an install record of theirs is removed (module binding and record only; the "
            + "partition's data stays).",
            proven.Count, string.Join(", ", proven.Select(p => $"{p.Source}/{p.PackageId}")));
        return DeclaredDisabledRetirement.Retire(hub, CompositionOf(proven), logger);
    }
}
