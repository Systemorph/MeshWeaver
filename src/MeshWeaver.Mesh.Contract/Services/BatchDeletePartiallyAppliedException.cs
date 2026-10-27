using System.Collections.Immutable;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// The fault of an <see cref="IStorageAdapter.DeleteMany"/> that removed SOME of its rows before it
/// failed, naming exactly the rows that are gone (MeshWeaver#7396).
///
/// <para><b>Why it exists.</b> A batch delete reported what it removed only when it SUCCEEDED. A
/// batch that faulted part-way handed its caller a bare exception, so a move or a delete could not
/// tell which source rows had already left storage, and could not retire the per-node hubs and
/// stream-cache entries at those paths. Those hubs went on serving under addresses that name no
/// node, which is the moved-away-address defect of #7171 surviving into the failure path.</para>
///
/// <para><b>The contract.</b> A <see cref="IStorageAdapter.DeleteMany"/> that faults after removing
/// rows faults with THIS type, and <see cref="Removed"/> is the set that left storage, children
/// before parents. Any other exception means the adapter confirmed no removal. A caller reads the
/// set through <see cref="RemovedBy"/>, which answers empty for every other exception, so it never
/// has to know which kind it got.</para>
/// </summary>
public sealed class BatchDeletePartiallyAppliedException : Exception
{
    /// <summary>Creates the fault for a batch that removed <paramref name="removed"/> and then failed with <paramref name="cause"/>.</summary>
    /// <param name="removed">The rows the batch removed before it failed, children before parents.</param>
    /// <param name="cause">The failure that stopped the batch.</param>
    public BatchDeletePartiallyAppliedException(IReadOnlyList<string> removed, Exception cause)
        : base($"The batch delete failed after removing {removed.Count} row(s): {cause.Message}", cause)
    {
        Removed = removed;
    }

    /// <summary>The rows the batch removed before it failed, children before parents.</summary>
    public IReadOnlyList<string> Removed { get; }

    /// <summary>
    /// The rows <paramref name="error"/> says a batch delete removed: <see cref="Removed"/> for this
    /// type, empty for every other exception.
    /// </summary>
    /// <param name="error">The fault a <see cref="IStorageAdapter.DeleteMany"/> raised.</param>
    public static IReadOnlyList<string> RemovedBy(Exception error)
        => error is BatchDeletePartiallyAppliedException partial ? partial.Removed : [];

    /// <summary>
    /// <paramref name="error"/>, carrying <paramref name="removedBefore"/> as well as whatever it
    /// already names. Unchanged when <paramref name="removedBefore"/> is empty, so a fault that removed
    /// nothing keeps its own type and message. A fault that already names rows is unwrapped and
    /// re-wrapped once, never nested.
    /// </summary>
    /// <param name="error">The fault to carry the set on.</param>
    /// <param name="removedBefore">Rows removed BEFORE the part of the batch that raised <paramref name="error"/>.</param>
    public static Exception Including(Exception error, IReadOnlyCollection<string> removedBefore)
    {
        if (removedBefore.Count == 0)
            return error;
        return error is BatchDeletePartiallyAppliedException partial
            ? new BatchDeletePartiallyAppliedException(
                removedBefore.Concat(partial.Removed).Distinct(StringComparer.Ordinal).ToImmutableList(),
                partial.InnerException ?? partial)
            : new BatchDeletePartiallyAppliedException(removedBefore.ToImmutableList(), error);
    }
}
