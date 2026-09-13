namespace MeshWeaver.Mesh.Security;

/// <summary>
/// 🚨 A package the registry itself declares in an instance's DEFAULT SET (<c>preInstalled</c>)
/// that the instance's plan does not cover — returned to the consumer as a typed verdict rather
/// than as an omission (#4097).
///
/// <para><b>Why this exists.</b> Until #4097 a plan-tier refusal was indistinguishable from "no
/// such package" on the wire — the enumeration defence — and was logged on the REGISTRY, where
/// the consumer cannot read it. A free instance therefore booted fine, quietly lacked the
/// enterprise <c>Hosting</c> package, and every surface on the instance named a consequence
/// (<c>canPatch=False</c>, "required module not installed — install the package from the
/// registry") and never the cause. An hour was spent on RBAC, module delivery and a design change
/// before the plan was suspected (measured 2026-09-12 on <c>build.meshweaver.cloud</c>).</para>
///
/// <para><b>What it is not.</b> The enumeration defence is about UNGRANTED SOURCES: a refusal for
/// a package outside the instance's granted sources stays byte-identical to absence, so a
/// registered instance can never learn what else a registry carries. A pre-installed package the
/// registry declares in the instance's own default set is not enumeration — the registry chose to
/// tell every instance about it — and <see cref="PluginGrant.TierRefusal"/> answers ONLY when
/// some grant entry reaches the package and the plan is what refuses it.</para>
/// </summary>
/// <param name="PackageId">The package the plan does not cover (e.g. <c>Hosting</c>).</param>
/// <param name="Module">The compiled module the package delivers, when it declares one
/// (<c>MeshWeaver.SelfUpdate.Aks</c>) — what <c>Modules:Required</c> and the activation record are
/// keyed by; null for a content-only package.</param>
/// <param name="RequiredTier">The tier the package declares (<c>enterprise</c>).</param>
/// <param name="InstancePlan">The plan the decision was taken at: the instance's own plan
/// (<c>free</c> when the record carries none), narrowed by the reaching entry's cap when it
/// names one — so the sentence names the plan that actually refused, never a plan the instance
/// holds but the entry does not license.</param>
public sealed record PlanTierRefusal(
    string PackageId, string? Module, string RequiredTier, string InstancePlan)
{
    /// <summary>
    /// The one sentence every consumer surface says, in the vocabulary #4091 introduced for a
    /// binding conflict ("⛔ Not installed on this platform: it needs X, this platform provides
    /// Y") with the nouns of a plan: <c>⛔ Not installed on this instance: Hosting needs plan tier
    /// enterprise, this instance is on free.</c> Operator-facing (logs, <c>/health</c>); the
    /// package card renders the same data through a localized key.
    /// </summary>
    public string Describe() =>
        $"⛔ Not installed on this instance: {PackageId} needs plan tier {RequiredTier}, "
        + $"this instance is on {InstancePlan}.";

    /// <summary>Whether this refusal is about the module <paramref name="moduleName"/> (simple
    /// assembly name, compared case-insensitively).</summary>
    public bool IsForModule(string? moduleName) =>
        !string.IsNullOrWhiteSpace(Module)
        && string.Equals(Module, moduleName, StringComparison.OrdinalIgnoreCase);
}
