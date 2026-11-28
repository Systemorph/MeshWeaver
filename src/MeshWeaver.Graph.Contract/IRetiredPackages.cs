using System.Collections.Immutable;

namespace MeshWeaver.Graph;

/// <summary>
/// What the registry has PROVEN retired — the declaration the compile control plane reads so a
/// retired package's types are skipped quietly instead of being parked with an Error on every boot
/// (MeshWeaver#8619, <c>Doc/Architecture/RetiredPackageConvergence</c>).
///
/// <para><b>Declared, never derived.</b> A package is in this ledger ONLY because a successful
/// registry listing carried it in its explicit <c>retired</c> set. A package that is merely absent
/// from a listing — an outage, a partial listing, a source the caller is not granted — is never
/// declared: an absent contributor is not a removal.</para>
/// </summary>
public interface IRetiredPackages
{
    /// <summary>The declaration for the partition <paramref name="partition"/> (the top-level path
    /// segment — a package installs into the partition of its id), or null when the registry has not
    /// declared its package retired.</summary>
    /// <param name="partition">The partition.</param>
    RetiredPackageDeclaration? For(string partition);

    /// <summary>
    /// Records one registry listing's retired set. Replaces the previous declaration of the same
    /// source, so a package the registry stops declaring (re-introduced) stops being retired. Called
    /// only with a SUCCESSFUL listing.
    /// </summary>
    /// <param name="source">The package source the listing came from.</param>
    /// <param name="retired">The retired packages that listing declared and did not also list.</param>
    void Declare(string source, IReadOnlyCollection<RetiredPackageDeclaration> retired);

    /// <summary>Says, once per process per partition, that the package is retired and its types are
    /// skipped. Returns true the first time it is asked about a partition.</summary>
    /// <param name="partition">The partition.</param>
    bool FirstSighting(string partition);

    /// <summary>True while this boot has announced a registry listing (<see cref="ExpectListing"/>) that has
    /// neither answered nor failed yet — a gate about to park a type waits for it (bounded), so the boot
    /// race between the compile watcher and the listing never writes the Error the declaration prevents.</summary>
    bool AwaitingListing { get; }

    /// <summary>Emits once the awaited listing answered or failed (replayed to a late subscriber).</summary>
    IObservable<System.Reactive.Unit> WhenListed { get; }

    /// <summary>Announces that a registry listing is about to be read this boot.</summary>
    void ExpectListing();

    /// <summary>Releases <see cref="WhenListed"/>: the listing answered (<see cref="Declare"/> does this) or
    /// the pass ended without one.</summary>
    void MarkListed();
}

/// <summary>One package the registry declares retired.</summary>
/// <param name="Source">The package source (registry source name) that declared it.</param>
/// <param name="PackageId">The package id (also its partition).</param>
/// <param name="Reason">Why, as the registry states it; may be empty.</param>
public sealed record RetiredPackageDeclaration(string Source, string PackageId, string Reason);

/// <summary>
/// The in-memory <see cref="IRetiredPackages"/>: an instance field on a mesh-scoped singleton, never
/// static, so nothing bleeds between meshes or tests.
/// </summary>
public sealed class RetiredPackageLedger : IRetiredPackages
{
    private ImmutableDictionary<string, ImmutableList<RetiredPackageDeclaration>> bySource =
        ImmutableDictionary.Create<string, ImmutableList<RetiredPackageDeclaration>>(StringComparer.OrdinalIgnoreCase);

    private ImmutableHashSet<string> said = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public RetiredPackageDeclaration? For(string partition) =>
        string.IsNullOrWhiteSpace(partition)
            ? null
            : Volatile.Read(ref bySource).Values
                .SelectMany(list => list)
                .FirstOrDefault(d => string.Equals(d.PackageId, partition, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public void Declare(string source, IReadOnlyCollection<RetiredPackageDeclaration> retired)
    {
        ImmutableInterlocked.Update(ref bySource, static (map, arg) =>
            arg.Retired.Count == 0 ? map.Remove(arg.Source) : map.SetItem(arg.Source, [.. arg.Retired]),
            (Source: source ?? "", Retired: retired));
        MarkListed();
    }

    private readonly System.Reactive.Subjects.BehaviorSubject<bool> listed = new(false);
    private int expecting;

    /// <inheritdoc />
    public bool AwaitingListing => Volatile.Read(ref expecting) == 1 && !listed.Value;

    /// <inheritdoc />
    public IObservable<System.Reactive.Unit> WhenListed =>
        System.Reactive.Linq.Observable.Select(
            System.Reactive.Linq.Observable.Take(System.Reactive.Linq.Observable.Where(listed, l => l), 1),
            _ => System.Reactive.Unit.Default);

    /// <inheritdoc />
    public void ExpectListing() => Interlocked.Exchange(ref expecting, 1);

    /// <inheritdoc />
    public void MarkListed() => listed.OnNext(true);

    /// <inheritdoc />
    public bool FirstSighting(string partition) =>
        ImmutableInterlocked.Update(ref said, static (set, p) => set.Add(p), partition);
}
