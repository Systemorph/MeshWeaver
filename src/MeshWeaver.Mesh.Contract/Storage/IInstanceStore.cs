using System.Collections.Immutable;

namespace MeshWeaver.Mesh.Storage;

/// <summary>
/// One of the instance's PRE-CONFIGURED stores — discovered from its own configuration and
/// identity (the Postgres the mesh runs on, the blob account behind its content collections, a
/// file-system root) and registered in DI by the module that owns the connection. The storage
/// settings list these, read their containers live, and create new containers in them with the
/// instance's own identity. Implementations do their I/O through <c>IIoPool</c>; every member is
/// cold and reactive.
/// </summary>
public interface IInstanceStore
{
    /// <summary>A stable id, unique on the instance (e.g. <c>postgres</c>, <c>content-blob</c>).</summary>
    string Id { get; }

    /// <summary>The store's kind (<see cref="StorageStoreKind"/>).</summary>
    string Kind { get; }

    /// <summary>A human-readable name — never carrying a credential.</summary>
    string DisplayName { get; }

    /// <summary>The purposes this store serves when NO binding applies (the instance default).</summary>
    IReadOnlySet<string> DefaultPurposes { get; }

    /// <summary>
    /// The container a partition uses here when nothing is bound — exactly what the instance does
    /// today (Postgres: the partition's own schema). Pure.
    /// </summary>
    /// <param name="partition">The partition.</param>
    string DefaultContainerFor(string partition);

    /// <summary>
    /// The name prefix that marks a container as <paramref name="partition"/>'s OWN in this store
    /// (<see cref="StorageContainerOwnership"/>). Defaults to <see cref="DefaultContainerFor"/>, which
    /// is right when the default container is per partition (a Postgres schema); a store whose
    /// default container is SHARED (one blob container for every collection) overrides it with a
    /// per-partition name. Pure.
    /// </summary>
    /// <param name="partition">The partition.</param>
    string ContainerPrefixFor(string partition) => DefaultContainerFor(partition);

    /// <summary>Why <paramref name="name"/> cannot be a container in this store, or <c>null</c> when
    /// it can. Pure — the store's own naming rule, checked before anything is sent.</summary>
    /// <param name="name">The proposed container name.</param>
    string? ValidateName(string name);

    /// <summary>The containers that exist in the store now, read live (sorted, de-duplicated).</summary>
    IObservable<ImmutableList<string>> ListContainers();

    /// <summary>
    /// Creates <paramref name="name"/> if it does not exist — IDEMPOTENT: a second call for the same
    /// name answers <see cref="StorageProbe.Exists"/> with nothing created. Runs with the instance's
    /// own identity. A malformed name is answered, never sent.
    /// </summary>
    /// <param name="name">The container to ensure.</param>
    IObservable<StorageProbe> EnsureContainer(string name);

    /// <summary>Checks <paramref name="name"/>: reachable, exists, and what the store said.</summary>
    /// <param name="name">The container to check.</param>
    IObservable<StorageProbe> Probe(string name);
}

/// <summary>
/// One store's answer about one container.
/// </summary>
/// <param name="Container">The container asked about.</param>
/// <param name="Reachable">The store answered at all (and accepted the instance's identity).</param>
/// <param name="Exists">The container exists now.</param>
/// <param name="Created">This call created it (false on an idempotent repeat).</param>
/// <param name="Message">What the store said — the reason on a failure.</param>
public sealed record StorageProbe(string Container, bool Reachable, bool Exists, bool Created, string? Message = null)
{
    /// <summary>A name the store refuses — answered without contacting it.</summary>
    /// <param name="container">The refused name.</param>
    /// <param name="reason">Why.</param>
    public static StorageProbe Refused(string container, string reason)
        => new(container, Reachable: true, Exists: false, Created: false, reason) { NameRefused = true };

    /// <summary>The store could not be reached.</summary>
    /// <param name="container">The container asked about.</param>
    /// <param name="reason">The store's words.</param>
    public static StorageProbe Unreachable(string container, string reason)
        => new(container, Reachable: false, Exists: false, Created: false, reason);

    /// <summary>True when the name itself was refused by the store's naming rule.</summary>
    public bool NameRefused { get; init; }

    /// <summary>True when the instance's identity may write to the container (create tables or
    /// objects in it). A container that exists but refuses the instance is not a usable target.</summary>
    public bool Writable { get; init; } = true;
}

/// <summary>
/// Which containers a partition may bind. 🚨 The instance's stores are SHARED: every partition's
/// default schema sits in the same Postgres, so a partition that could bind any existing schema
/// could bind ANOTHER partition's — and read or overwrite its data. So a partition may use only
/// its default container in the store or a container named for it — its
/// <see cref="IInstanceStore.ContainerPrefixFor"/>, alone or followed by <c>_</c>/<c>-</c> and more
/// (<c>acme</c>, <c>acme_parts</c>, <c>acme-originals</c>); the <c>Admin</c> partition — whose
/// bindings only a platform admin can write — may use any. The resolver re-applies this rule
/// before it honours a binding, so a forged verdict on a node cannot widen it.
/// </summary>
public static class StorageContainerOwnership
{
    /// <summary>True when <paramref name="partition"/> may bind <paramref name="container"/> in <paramref name="store"/>.</summary>
    /// <param name="store">The store.</param>
    /// <param name="partition">The partition holding the binding.</param>
    /// <param name="container">The container.</param>
    public static bool MayUse(IInstanceStore store, string partition, string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
            return false;
        if (StorageBindingPaths.IsAdmin(partition))
            return true;
        if (string.Equals(container, store.DefaultContainerFor(partition), StringComparison.OrdinalIgnoreCase))
            return true;
        var own = store.ContainerPrefixFor(partition);
        return !string.IsNullOrEmpty(own)
               && (string.Equals(container, own, StringComparison.OrdinalIgnoreCase)
                   || (container.Length > own.Length + 1
                       && container.StartsWith(own, StringComparison.OrdinalIgnoreCase)
                       && container[own.Length] is '_' or '-'));
    }

    /// <summary>
    /// <see cref="MayUse(IInstanceStore, string, string?)"/> AND not ANOTHER partition's default
    /// container — the hole the prefix rule alone leaves (a partition <c>acme_x</c> next to a partition
    /// <c>acme</c>). <paramref name="knownPartitions"/> is the partition catalog; <c>null</c> means it
    /// could not be read, and then only the partition's own default container is allowed (fail closed).
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="partition">The partition holding the binding.</param>
    /// <param name="container">The container.</param>
    /// <param name="knownPartitions">Every partition of the mesh, or <c>null</c> when unknown.</param>
    public static bool MayUse(IInstanceStore store, string partition, string? container, IEnumerable<string>? knownPartitions)
    {
        if (!MayUse(store, partition, container))
            return false;
        if (StorageBindingPaths.IsAdmin(partition)
            || string.Equals(container, store.DefaultContainerFor(partition), StringComparison.OrdinalIgnoreCase))
            return true;
        return knownPartitions is not null
               && OwnerOfDefault(store, partition, container!, knownPartitions) is null;
    }

    /// <summary>The partition other than <paramref name="partition"/> whose default container in
    /// <paramref name="store"/> is <paramref name="container"/>, or <c>null</c>. Pure.</summary>
    /// <param name="store">The store.</param>
    /// <param name="partition">The partition holding the binding.</param>
    /// <param name="container">The container.</param>
    /// <param name="knownPartitions">Every partition of the mesh.</param>
    public static string? OwnerOfDefault(IInstanceStore store, string partition, string container, IEnumerable<string> knownPartitions)
        => knownPartitions.FirstOrDefault(p => !string.Equals(p, partition, StringComparison.OrdinalIgnoreCase)
                                               && string.Equals(store.DefaultContainerFor(p), container, StringComparison.OrdinalIgnoreCase));

    /// <summary>The containers of <paramref name="all"/> that <paramref name="partition"/> may bind.</summary>
    /// <param name="store">The store.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="all">Every container of the store.</param>
    public static ImmutableList<string> Usable(IInstanceStore store, string partition, IEnumerable<string> all)
        => [.. all.Where(c => MayUse(store, partition, c))];
}
