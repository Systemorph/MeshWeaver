namespace MeshWeaver.Mesh.Services;

/// <summary>What the cluster knows about one member.</summary>
public enum ClusterMemberState
{
    /// <summary>
    /// The member is running — joining, active, or shutting down. Deliberately includes the
    /// shutting-down states: that process is still executing, so anything it holds is still held.
    /// </summary>
    Alive,

    /// <summary>
    /// The cluster has POSITIVELY recorded this member as departed. Only this value licenses taking
    /// over what it held.
    /// </summary>
    Gone,

    /// <summary>
    /// No opinion — there is no cluster (monolith, test, dev box), the identity is unparseable or
    /// absent, or membership simply has no record of it. 🚨 Never confuse this with
    /// <see cref="Gone"/>: "I do not know" must never authorise a takeover, or the whole point of
    /// asking membership instead of a clock is lost.
    /// </summary>
    Unknown
}

/// <summary>
/// Whether a process that stamped its identity somewhere is still a member of this cluster.
///
/// <para><b>Why this exists.</b> Coordination artefacts on a shared volume — the NodeType bake lease
/// above all — have to answer "is the holder still alive?", and a file timestamp is a poor way to
/// ask: it says only when the holder last managed to write, which conflates "dead" with "busy",
/// "starved", and "the SMB metadata read was cached". Where a cluster exists, its membership service
/// already answers the real question authoritatively and immediately, so the timestamp becomes a
/// fallback for hosts that have no cluster rather than the mechanism.</para>
///
/// <para><b>The identity is opaque and self-consistent by design.</b> A caller stamps
/// <see cref="LocalIdentity"/> and later hands the same string back to <see cref="StateOf"/>; only
/// the implementation ever interprets it. That deliberately avoids the fragile step of mapping a pod
/// or machine name onto a cluster member — an implementation resolves its own identity exactly, and
/// a string it cannot resolve is <see cref="ClusterMemberState.Unknown"/>, never
/// <see cref="ClusterMemberState.Gone"/>.</para>
///
/// <para>Registered by the cluster host (the Orleans silo). Its absence means "no cluster", which
/// every consumer must handle as <see cref="ClusterMemberState.Unknown"/>.</para>
/// </summary>
public interface IClusterMembership
{
    /// <summary>
    /// This process's own membership identity — the string to stamp on anything whose holder may
    /// later need resolving. Stable for the process's lifetime.
    /// </summary>
    string LocalIdentity { get; }

    /// <summary>
    /// What the cluster knows about the process that stamped <paramref name="identity"/>. Never
    /// throws: anything it cannot answer is <see cref="ClusterMemberState.Unknown"/>.
    /// </summary>
    ClusterMemberState StateOf(string identity);
}
