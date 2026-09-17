namespace MeshWeaver.Plugin.Packaging;

/// <summary>
/// The ONE spelling of the two findings a module bundle's closure derivation reports for what it
/// cannot carry (#4126, #4367) — shared by BOTH derivations, the SDK lane's
/// (<c>MeshWeaver.Plugin.Build.DepsClosure</c>, which REFUSES the pack unless something named
/// carries it) and the container lane's (<c>MeshWeaver.PluginTester</c>, which NAMES it as a
/// warning until a measured container wave arms a refusal there, #4445).
///
/// <para>🚨 One spelling because the arming decision is a MEASUREMENT: "a full wave printed zero
/// of either finding" is a grep for these sentences across every pack log, and two lanes wording
/// the same finding two ways would let a wave read clean on one lane while the other said it in
/// words nobody grepped for. Each lane appends its OWN remedy — the step a module can actually
/// take on that lane — so only the finding is shared, never the advice.</para>
/// </summary>
public static class UncarriedAssetFindings
{
    /// <summary>A RID-specific MANAGED assembly (<c>assetType: "runtime"</c> under
    /// <c>runtimeTargets</c>) the closure does not carry.</summary>
    /// <param name="package">The package that declares it.</param>
    /// <param name="relativePath">Its deps.json key, <c>/</c>-separated.</param>
    public static string RidSpecificManaged(string package, string relativePath) =>
        $"'{package}' declares a RID-specific MANAGED asset the bundle does not carry: "
        + $"{relativePath}. The module's flat closure has one slot per assembly name and no way to "
        + "choose a RID at pack time.";

    /// <summary>A native payload declared at a layout the module loader does not probe (anything
    /// but exactly <c>runtimes/&lt;rid&gt;/native/&lt;file&gt;</c>).</summary>
    /// <param name="package">The package that declares it.</param>
    /// <param name="relativePath">Its deps.json key, <c>/</c>-separated.</param>
    public static string UnprobedNative(string package, string relativePath) =>
        $"'{package}' declares a native asset at '{relativePath}', which is NOT the layout the "
        + "module loader probes (exactly runtimes/<rid>/native/<file>) — it is not carried, "
        + "because bytes at a path nothing looks at read as shipped and behave as absent.";
}
