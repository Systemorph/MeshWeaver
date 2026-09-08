namespace MeshWeaver.Hosting.SelfUpdate;

// Extracted from UpdatePolicyNodeType when the AKS mechanics moved to a module: this is the policy
// VALUE, which both the platform's poller options and the module's mechanics read. The NodeType
// REGISTRATION stays in the portal, so existing Admin/UpdatePolicy nodes keep deserializing
// whether or not the module is listed.

/// <summary>
/// The platform auto-update strategy — the single value on <c>Admin/UpdatePolicy</c>.
///
/// <para>🚨 <b>The default is <see cref="Stable"/> — clean releases only (maintainer, 2026-09-08):</b>
/// <i>"by default we will not upgrade as long as no version without <c>-ci…</c> is labelled ⇒ we
/// want to have a clean label <c>3.0.1</c> to upgrade."</i> A continuous build (<c>X.Y.Z-ci.&lt;n&gt;</c>)
/// is taken only under <see cref="Continuous"/> AND only when the record's version pattern admits
/// it (<see cref="UpdateChannelPattern"/>, e.g. <c>3.0.1-ci*</c>); <see cref="Continuous"/> with no
/// pattern behaves as <see cref="Stable"/> and says so once at Warning, naming the pattern to set.</para>
///
/// <para>🚨 The enum ORDER is binary contract: the hub serializer drops whichever member is zero on
/// write (<c>WhenWritingDefault</c>), which is why <c>UpdatePolicyContent.DeclaredPolicy</c> is
/// nullable rather than this enum being reordered to put the default first. Do not reorder.</para>
/// </summary>
public enum UpdatePolicyKind
{
    /// <summary>Follow the continuous builds the policy record's version PATTERN admits (e.g.
    /// <c>3.0.1-ci*</c> ⇒ every sealed <c>3.0.1-ci.&lt;n&gt;</c>, newest run first). Without a pattern
    /// this is <see cref="Stable"/>: no pre-release is ever eligible on its own.</summary>
    Continuous,

    /// <summary>Roll only to the newest CLEAN release (no pre-release label, e.g. <c>3.0.1</c>);
    /// ignore every continuous build. <b>This is the platform default.</b></summary>
    Stable,

    /// <summary>Never auto-update. Updates are applied manually (operator / admin action).</summary>
    None,
}
