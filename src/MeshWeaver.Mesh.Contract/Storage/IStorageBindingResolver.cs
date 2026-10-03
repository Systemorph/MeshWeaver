namespace MeshWeaver.Mesh.Storage;

/// <summary>
/// Where one purpose of one partition is stored — the answer of <see cref="IStorageBindingResolver"/>.
/// </summary>
/// <param name="Purpose">The purpose resolved (<see cref="StoragePurpose"/>).</param>
/// <param name="Partition">The partition it was resolved for.</param>
/// <param name="StoreId">The store (<see cref="IInstanceStore.Id"/>); <see cref="StorageStoreKind.Instance"/>
/// when no store is registered for the purpose.</param>
/// <param name="StoreKind">The store's kind (<see cref="StorageStoreKind"/>).</param>
/// <param name="Container">The container: a Postgres schema, a blob container, a directory.</param>
public sealed record ResolvedStorage(
    string Purpose, string Partition, string StoreId, string StoreKind, string Container)
{
    /// <summary>A prefix for the tables the purpose creates, when the binding names one.</summary>
    public string? TablePrefix { get; init; }

    /// <summary>The binding node that decided this answer; <c>null</c> for the built-in default.</summary>
    public string? BindingPath { get; init; }

    /// <summary>
    /// True for the built-in default — no binding applies. A consumer reads it as
    /// "do exactly what you did before bindings existed"; only a non-default answer needs rewiring.
    /// </summary>
    public bool IsDefault => BindingPath is null;

    /// <summary>The vault reference of a separate account (a Key Vault secret NAME), when the binding
    /// names one. Never a secret.</summary>
    public string? VaultReference { get; init; }
}

/// <summary>
/// Resolves where a partition stores one purpose. Registered on every mesh; the default
/// implementation reads the binding nodes at <c>{partition}/_Storage</c> (and, for an instance-wide
/// purpose, <c>Admin/_Storage</c>) as the infrastructure identity.
/// </summary>
public interface IStorageBindingResolver
{
    /// <summary>
    /// The location of <paramref name="purpose"/> for <paramref name="partition"/>, LIVE: emits the
    /// current answer at once and again whenever a binding that decides it changes (de-duplicated).
    /// Take the first value when the answer is needed once, at open.
    ///
    /// <para>Order: the partition's own USABLE binding for the purpose (one naming
    /// <paramref name="collection"/> before its default one) → for an instance-wide purpose
    /// (<see cref="StoragePurpose.FallsBackToAdmin"/>) the <c>Admin</c> partition's → the built-in
    /// default (<see cref="ResolvedStorage.IsDefault"/>). Never errors and never stays silent: a
    /// binding read that fails resolves to the default and says so in the log.</para>
    /// </summary>
    /// <param name="purpose">The purpose (<see cref="StoragePurpose"/>).</param>
    /// <param name="partition">The partition (its root path; <c>Admin</c> for the instance-wide store).</param>
    /// <param name="collection">The content collection asking, when the purpose is per collection.</param>
    IObservable<ResolvedStorage> Resolve(string purpose, string partition, string? collection = null);
}
