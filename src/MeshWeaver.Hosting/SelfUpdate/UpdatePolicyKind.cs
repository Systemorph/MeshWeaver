namespace MeshWeaver.Hosting.SelfUpdate;

// Extracted from UpdatePolicyNodeType when the AKS mechanics moved to a module: this is the policy
// VALUE, which both the platform's poller options and the module's mechanics read. The NodeType
// REGISTRATION stays in the portal, so existing Admin/UpdatePolicy nodes keep deserializing
// whether or not the module is listed.

/// <summary>The platform auto-update strategy — the single value on <c>Admin/UpdatePolicy</c>.</summary>
public enum UpdatePolicyKind
{
    /// <summary>Always roll to the newest image on ACR, INCLUDING build-numbered continuous builds
    /// (e.g. <c>3.0.0-ci.51</c>). This is the platform default.</summary>
    Continuous,

    /// <summary>Roll only to the newest CLEAN release (no build number, e.g. <c>3.0.0</c>); ignore
    /// continuous build-numbered images.</summary>
    Stable,

    /// <summary>Never auto-update. Updates are applied manually (operator / admin action).</summary>
    None,
}
