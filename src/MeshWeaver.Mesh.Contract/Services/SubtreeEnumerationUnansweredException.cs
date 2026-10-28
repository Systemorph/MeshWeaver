namespace MeshWeaver.Mesh.Services;

/// <summary>
/// The fault of an <see cref="IStorageAdapter.ListDescendantPaths"/> (or of a level of its default
/// <see cref="IStorageAdapter.ListChildPaths"/> walk) that COMPLETED WITHOUT EMITTING — a breach of
/// the contract "emits one complete snapshot and completes" (MeshWeaver#7461).
///
/// <para><b>Why it exists.</b> The enumeration is the authority the recursive-delete planner, its
/// drain verification, a move's delete leg and a space deletion's install-record read are built on.
/// The production composite folds every writable provider's answer into a SEEDED union, so a
/// provider that completed empty contributed nothing and the union still emitted: "no answer" read
/// as "no descendants", and a delete could verify a subtree as drained that nobody enumerated. An
/// empty completion is therefore turned into this fault at the provider, before any fold, and every
/// caller that already fails closed on a fault now fails closed on a missing answer too.</para>
///
/// <para>An adapter that answers "there is nothing under this root" emits an EMPTY collection; that
/// is an answer and never raises this.</para>
/// </summary>
public sealed class SubtreeEnumerationUnansweredException : InvalidOperationException
{
    /// <summary>Creates the fault for <paramref name="source"/> completing without an answer for <paramref name="rootPath"/>.</summary>
    /// <param name="source">The provider, adapter or walk level that gave no answer.</param>
    /// <param name="rootPath">The root whose subtree was being enumerated.</param>
    public SubtreeEnumerationUnansweredException(string source, string? rootPath)
        : base($"The subtree enumeration of '{rootPath}' by {source} completed without an answer; "
            + "the subtree is UNKNOWN, not empty.")
    {
        Provider = source;
        RootPath = rootPath;
    }

    /// <summary>The provider, adapter or walk level that gave no answer.</summary>
    public string Provider { get; }

    /// <summary>The root whose subtree was being enumerated.</summary>
    public string? RootPath { get; }
}

/// <summary>
/// Turns an empty completion of a subtree enumeration into a
/// <see cref="SubtreeEnumerationUnansweredException"/> (MeshWeaver#7461).
/// </summary>
internal static class SubtreeEnumeration
{
    /// <summary>
    /// Passes every emission of <paramref name="source"/> through and, when it completes WITHOUT
    /// emitting, faults with <see cref="SubtreeEnumerationUnansweredException"/> naming
    /// <paramref name="who"/>. Faults of <paramref name="source"/> propagate unchanged. Cold.
    /// </summary>
    internal static IObservable<T> RequireAnswer<T>(this IObservable<T> source, string who, string? rootPath)
        => System.Reactive.Linq.Observable.SelectMany(
            System.Reactive.Linq.Observable.DefaultIfEmpty(
                System.Reactive.Linq.Observable.Select(source, value => (Answered: true, Value: value)),
                (Answered: false, Value: default(T)!)),
            one => one.Answered
                ? System.Reactive.Linq.Observable.Return(one.Value)
                : System.Reactive.Linq.Observable.Throw<T>(
                    new SubtreeEnumerationUnansweredException(who, rootPath)));
}
