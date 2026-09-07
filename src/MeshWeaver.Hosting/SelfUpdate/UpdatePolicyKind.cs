namespace MeshWeaver.Hosting.SelfUpdate;

// Extracted from UpdatePolicyNodeType when the AKS mechanics moved to a module: this is the policy
// VALUE, which both the platform's poller options and the module's mechanics read. The NodeType
// REGISTRATION stays in the portal, so existing Admin/UpdatePolicy nodes keep deserializing
// whether or not the module is listed.

/// <summary>The platform auto-update strategy — the single value on <c>Admin/UpdatePolicy</c>.</summary>
public enum UpdatePolicyKind
{
    /// <summary>
    /// Never auto-update. Updates are applied manually (operator / admin action).
    ///
    /// <para>🚨 <b>Deliberately the ZERO value — the order of this enum is load-bearing (#3542).</b>
    /// The hub serializer sets <c>DefaultIgnoreCondition = WhenWritingDefault</c>, so whichever
    /// member is zero is OMITTED when written, and an omitted field reads back as that member.
    /// While <see cref="Continuous"/> was zero, a policy record that lost its <c>policy</c> field
    /// read back as <i>auto-update enabled</i> — the most permissive state, reached by losing
    /// information. That is how memex-cloud rolled onto a withdrawn line "on a policy record that
    /// lost its own policy". With <c>None</c> at zero the same loss fails CLOSED.</para>
    ///
    /// <para>Members travel BY NAME (<c>EnumMemberJsonStringEnumConverter</c> is global), so records
    /// already storing <c>"Continuous"</c> or <c>"Stable"</c> are unaffected by the reorder.</para>
    /// </summary>
    None,

    /// <summary>Always roll to the newest image on ACR, INCLUDING build-numbered continuous builds
    /// (e.g. <c>3.0.0-ci.51</c>). The platform default for a NEWLY created record — but no longer
    /// what an ABSENT value means.</summary>
    Continuous,

    /// <summary>Roll only to the newest CLEAN release (no build number, e.g. <c>3.0.0</c>); ignore
    /// continuous build-numbered images.</summary>
    Stable,
}
