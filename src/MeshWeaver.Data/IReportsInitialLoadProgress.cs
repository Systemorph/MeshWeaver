namespace MeshWeaver.Data;

/// <summary>
/// Optional capability of an <see cref="ITypeSource"/> whose initial load is itself more than one
/// wait, so the <see cref="DataContext"/> init time-box can say WHICH of them was outstanding when it
/// expired rather than only that the type source's leg was (Systemorph/MeshWeaver#1122).
///
/// <para>The canonical case is the per-node hub's <c>MeshNodeTypeSource</c>: its one leg is a
/// durable storage read CONCATENATED ahead of the routing-supplied own-node stream. A pending
/// <c>…/MeshNode</c> leg therefore did not separate "a storage read never came back" from "the
/// routing stream never delivered an acceptable node" — the one question the attributed timeout
/// exists to answer.</para>
///
/// <para><b>Diagnostic only, and it runs on a failure path.</b> An implementation reads
/// presence-only state it already holds: it must not create, block, subscribe or throw, and nothing
/// may ever branch on what it returns.</para>
/// </summary>
public interface IReportsInitialLoadProgress
{
    /// <summary>
    /// One short sentence naming the part of the initial load that is still outstanding, or
    /// <c>null</c> when there is nothing more specific to say than the leg itself.
    /// </summary>
    /// <returns>The progress description, or <c>null</c>.</returns>
    string? DescribeInitialLoadProgress();
}
