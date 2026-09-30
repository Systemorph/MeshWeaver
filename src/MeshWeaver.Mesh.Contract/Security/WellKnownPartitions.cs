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

    /// <summary>
    /// 🚨 The PLATFORM's own partitions: <c>Admin</c> (a grant there IS platform administration),
    /// the <see cref="Mirror"/> pair, and the pseudo-identity / credential partitions. Together with
    /// <see cref="Fleet"/> this is the protected set of the <c>DeleteSpace</c> break-glass action
    /// (MeshWeaver.Plugins <c>Hosting/InstanceAction/Source/DeleteSpaceRunner.cs</c>,
    /// <c>ProtectedPartitions</c>), hosted HERE so both repositories read one list. None of them is
    /// ever a Space somebody lost: the platform-admin grant repair (#5904) refuses them outright.
    /// <c>_</c>-prefixed framework namespaces are refused by shape, not listed.
    /// </summary>
    public static readonly ImmutableHashSet<string> Platform = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "Admin", "Auth", "User", "Portal", "Kernel", "ApiToken", "system-security", "Anonymous");

    /// <summary>
    /// The FLEET's partitions — the records and shipped content every instance runs on. The rest of
    /// <c>DeleteSpaceRunner.ProtectedPartitions</c> (see <see cref="Platform"/>). The platform-admin
    /// grant repair (#5904) issues only an ENTITLEMENT here (no Admin/Editor), whatever their state.
    /// </summary>
    public static readonly ImmutableHashSet<string> Fleet = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "Hosting", "Hosting.Instance", "Deployments", "Ops", "Store", "Plugins", "Governance",
        "Doc", "Documentation", "Essentials", "Agent", "Skill", "Provider", "Providers", "Model", "AI",
        "Approvals", "Feedback", "Home");
}
