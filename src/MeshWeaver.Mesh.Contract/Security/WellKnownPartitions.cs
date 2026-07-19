using System.Collections.Immutable;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Well-known top-level partitions with special, framework-managed semantics — the canonical
/// source shared by the write guard, the self-healing partition bootstrap, and the admin
/// invariant so all three enforce the SAME notion of "system-managed".
/// </summary>
public static class WellKnownPartitions
{
    /// <summary>
    /// System-managed <b>lookup-mirror</b> partitions (User / Group / Role / VUser / ApiToken /
    /// EaCredential rows), written ONLY by the platform middleware / DB mirror trigger — never by
    /// an interactive user, not even a platform admin. Consequences, enforced consistently:
    /// <list type="bullet">
    ///   <item><c>PartitionWriteGuardValidator</c> rejects interactive Create/Update here.</item>
    ///   <item>The self-healing partition bootstrap never provisions a Space root or a
    ///     creator-Admin grant here — there is no user Space to bootstrap.</item>
    ///   <item>The "keep at least one admin" invariant does not apply — these have no user admin.</item>
    /// </list>
    /// Case-insensitive (<c>auth</c> ≡ <c>Auth</c>).
    /// </summary>
    public static readonly ImmutableHashSet<string> Mirror =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "User", "Auth");

    /// <summary>
    /// True when <paramref name="partition"/> is a system-managed mirror partition
    /// (<see cref="Mirror"/>). Expects a bare partition segment, e.g. <c>"Auth"</c>.
    /// </summary>
    public static bool IsMirror(string? partition) =>
        !string.IsNullOrEmpty(partition) && Mirror.Contains(partition);
}
