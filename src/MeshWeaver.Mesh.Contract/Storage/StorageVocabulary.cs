namespace MeshWeaver.Mesh.Storage;

/// <summary>
/// What a storage location is FOR. An OPEN vocabulary of string constants, spelled exactly as an
/// enum would be, so <c>StoragePurpose.DocParts</c> reads the same at every call site (policy
/// <c>open-vocabulary-string-constants</c>): a module may bind its own purpose in the same field
/// without a change here, and an unknown purpose stays unknown — it simply has no binding and
/// resolves to the instance default.
/// </summary>
public static class StoragePurpose
{
    /// <summary>Durable stream state (e.g. Orleans stream queues and checkpoints).</summary>
    public const string DurableStream = "DurableStream";

    /// <summary>The parts a document is cut into (the doc-part satellite tables).</summary>
    public const string DocParts = "DocParts";

    /// <summary>The ORIGINAL files of a content collection (the uploaded bytes).</summary>
    public const string Originals = "Originals";

    /// <summary>The vector index over content (the <c>content_chunks</c> store).</summary>
    public const string VectorIndex = "VectorIndex";

    /// <summary>The starting set the settings surface offers. Never a validation list — the set is open.</summary>
    public static IReadOnlyList<string> Known { get; } = [DurableStream, DocParts, Originals, VectorIndex];

    /// <summary>
    /// True for a purpose that is INSTANCE-WIDE by nature, so a partition without a binding of its
    /// own falls back to the <c>Admin</c> partition's binding before the built-in default. Only
    /// <see cref="DurableStream"/> today: a partition's doc parts, originals and index never borrow
    /// another partition's — not even the instance's — location.
    /// </summary>
    /// <param name="purpose">The purpose being resolved.</param>
    public static bool FallsBackToAdmin(string? purpose)
        => string.Equals(purpose, DurableStream, StringComparison.Ordinal);
}

/// <summary>
/// The kind of store a container lives in. Open string constants, like <see cref="StoragePurpose"/>.
/// </summary>
public static class StorageStoreKind
{
    /// <summary>A schema in a PostgreSQL database.</summary>
    public const string PostgresSchema = "PostgresSchema";

    /// <summary>A container in an Azure Blob Storage account.</summary>
    public const string BlobContainer = "BlobContainer";

    /// <summary>A directory under a file-system root.</summary>
    public const string Directory = "Directory";

    /// <summary>
    /// "Whatever the instance does today" — the kind a built-in default carries when no
    /// pre-configured store declares itself the default for the purpose.
    /// </summary>
    public const string Instance = "Instance";
}

/// <summary>
/// The validation verdict recorded ON a binding node. Open string constants: a reader that meets an
/// unknown value treats it as NOT valid — only <see cref="Valid"/> lets a binding override.
/// </summary>
public static class StorageValidationStatus
{
    /// <summary>Not validated yet (a fresh binding, or one whose target changed).</summary>
    public const string Pending = "Pending";

    /// <summary>The store is reachable and the container exists.</summary>
    public const string Valid = "Valid";

    /// <summary>The binding is refused: a malformed name, a container this partition may not use,
    /// a store that does not exist, or a container that does not exist.</summary>
    public const string Invalid = "Invalid";

    /// <summary>The store could not be reached, or refused the instance's identity.</summary>
    public const string Unreachable = "Unreachable";

    /// <summary>A separate account (a vault reference) the instance has no connector for, so it can
    /// neither verify nor use it. Never overrides.</summary>
    public const string Unverified = "Unverified";
}

/// <summary>
/// What a viewer asks the binding's own hub to do, written as <see cref="StorageBinding.RequestedAction"/>
/// (the RequestedX pattern: a write states the wish, the owning hub's watcher acts and clears it).
/// </summary>
public static class StorageBindingAction
{
    /// <summary>Re-validate the binding against its store.</summary>
    public const string Validate = "Validate";

    /// <summary>Create <see cref="StorageBinding.NewContainerName"/> in the store (idempotent) and
    /// bind to it.</summary>
    public const string Create = "Create";
}

/// <summary>
/// Where storage bindings live: <c>{partition}/_Storage/{id}</c>, a satellite of the partition root
/// held in its own <c>storage</c> table (<see cref="SatelliteTableMapping.Defaults"/>). The instance's
/// GLOBAL bindings are the <c>Admin</c> partition's own: <c>Admin/_Storage/{id}</c>.
/// </summary>
public static class StorageBindingPaths
{
    /// <summary>The NodeType of a storage binding node.</summary>
    public const string NodeType = "StorageBinding";

    /// <summary>The satellite segment under a partition root.</summary>
    public const string Segment = "_Storage";

    /// <summary>The satellite table the segment maps to.</summary>
    public const string Table = "storage";

    /// <summary>The partition that holds the instance-wide (global) bindings.</summary>
    public const string AdminPartition = "Admin";

    /// <summary>The container namespace of <paramref name="partition"/>'s bindings.</summary>
    /// <param name="partition">The partition (its root path, e.g. <c>acme</c> or <c>Admin</c>).</param>
    public static string NamespaceOf(string partition) => $"{partition}/{Segment}";

    /// <summary>The query listing <paramref name="partition"/>'s bindings.</summary>
    /// <param name="partition">The partition.</param>
    public static string QueryFor(string partition)
        => $"namespace:{NamespaceOf(partition)} nodeType:{NodeType}";

    /// <summary>The partition a path belongs to — its first segment; empty for an empty path.</summary>
    /// <param name="path">Any node path.</param>
    public static string PartitionOf(string? path)
        => string.IsNullOrEmpty(path) ? "" : path.Trim('/').Split('/', 2)[0];

    /// <summary>True when <paramref name="partition"/> is the Admin (global) partition.</summary>
    /// <param name="partition">The partition.</param>
    public static bool IsAdmin(string? partition)
        => string.Equals(partition, AdminPartition, StringComparison.OrdinalIgnoreCase);
}
