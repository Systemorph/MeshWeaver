using System.Collections.Immutable;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>The disk-derived inputs of a REQUIRED-modules probe, answered once per CHANGE of the
/// on-disk activation state rather than once per probe</b> (MeshWeaver#4608).
///
/// <para>Handed out by <see cref="PendingModuleActivations.ReadProbeInputs"/>, which reads them off
/// the same fingerprinted snapshot <see cref="PendingModuleActivations.Read()"/> uses. They are the
/// three expensive arguments of <see cref="RequiredModuleStatus.Classify(System.Collections.Generic.IEnumerable{string},System.Collections.Generic.IEnumerable{string},System.Collections.Generic.IReadOnlySet{string},System.Func{string,bool},ModuleActivationList,System.Func{ModuleActivationEntry,bool},System.Func{string,string},System.Collections.Generic.IReadOnlyCollection{Mesh.IncompatibleModule})"/>
/// — everything else that classification needs is configuration or in-process state, and costs
/// nothing.</para>
///
/// <para><b>Why a bundle rather than three accessors.</b> The three answers are only consistent
/// with each other while one fingerprint stands. Asking for them separately would let a caller
/// combine a sidecar read from before a landing with an existence probe from after it, and report a
/// module as enabled-but-absent that is neither — a state no operator could act on and no writer
/// ever produced. One call, one snapshot.</para>
///
/// <para>🚨 <b>What is deliberately NOT here: what this process has LOADED.</b> That half is cheap,
/// it changes without touching the volume, and memoising it would make a module that loaded since
/// the last probe keep reading as pending. The split is the whole rule #3664 established — the
/// volume once per change, the process every time.</para>
/// </summary>
/// <param name="Activation">The activation sidecar as of this snapshot, or <c>null</c> when it
/// could not be read — never an empty list standing in for one, which is how an unreadable record
/// used to report as "nothing pending".</param>
/// <param name="Unreadable">Every file that could not be parsed, and the open failure when the
/// sidecar itself could not be opened at all — one entry per file, because a probe that folds them
/// into one sentence loses the name an operator has to go and look at.</param>
/// <param name="ResolvesFromDeployment">Whether a declared module entry resolves to a file the
/// IMAGE carries — <c>MeshBuilder.ResolveModulePath(entry)</c>, the image's <c>modules/</c> tree
/// then the app closure. Memoised, because unmemoised it costs several metadata round trips per
/// entry per probe, and on a shared network volume that is the whole probe budget.
///
/// <para>🚨 It deliberately does NOT probe the landed tree: that is
/// <paramref name="LandedDllExists"/>'s question, and the classification depends on the two being
/// separable. Answering "yes" here for a landed module would report it Present and suppress the
/// reasons an operator actually needs — that its landing did not complete, or that the plan
/// refused it.</para></param>
/// <param name="LandedDllExists">Whether the landed assembly named by an activation entry is on the
/// volume. Memoised per generation directory for the life of the snapshot.</param>
public sealed record ModuleProbeInputs(
    ModuleActivationList? Activation,
    ImmutableList<string> Unreadable,
    Func<string, bool> ResolvesFromDeployment,
    Func<ModuleActivationEntry, bool> LandedDllExists)
{
    /// <summary>
    /// 🚨 <b>The image-side resolver of a probe that has taken NO reading yet</b>
    /// (MeshWeaver#4655) — and the one thing about it that matters is its REFERENCE IDENTITY:
    /// <see cref="RequiredModuleStatus.Classify(System.Collections.Generic.IEnumerable{string},System.Collections.Generic.IEnumerable{string},System.Collections.Generic.IReadOnlySet{string},System.Func{string,bool},ModuleActivationList,System.Func{ModuleActivationEntry,bool},System.Func{string,string},System.Collections.Generic.IReadOnlyCollection{Mesh.IncompatibleModule})"/>
    /// tests for THIS instance and answers <see cref="RequiredModuleState.Unmeasured"/>.
    ///
    /// <para><b>Why identity rather than a flag on this record.</b> The classifier takes the three
    /// expensive answers as loose arguments, not as this bundle, and every caller in the fleet
    /// passes them positionally. A new parameter would default to "measured" for each of them —
    /// i.e. every existing probe would keep reading "no record, therefore nothing is installed" out
    /// of a volume nobody had looked at. The signal has to ride the value that is actually handed
    /// over, and the value handed over is this delegate.</para>
    ///
    /// <para>It answers <c>false</c> for everything, which is the only honest answer an unread
    /// volume has — and on its own that answer is indistinguishable from "genuinely absent", which
    /// is the whole reason the identity is checked before the answer is used.</para>
    /// </summary>
    public static readonly Func<string, bool> VolumeNotRead = _ => false;

    /// <summary>
    /// The inputs of a probe taken before this process had read the module volume even once.
    ///
    /// <para>🚨 It is deliberately NOT an empty-but-valid reading: <see cref="Activation"/> is
    /// <c>null</c> (never an empty list standing in for one) and <see cref="Unreadable"/> names the
    /// condition, so every surface that already distinguishes "I read nothing" from "there is
    /// nothing" keeps distinguishing them.</para>
    /// </summary>
    /// <param name="moduleRoot">The deployment root the reading would have been taken from — named
    /// so an operator reading the payload knows WHICH volume has not been read.</param>
    /// <returns>Inputs that classify as <see cref="RequiredModuleState.Unmeasured"/>.</returns>
    public static ModuleProbeInputs NotRead(string moduleRoot) =>
        new(null,
            [
                $"the module volume under '{moduleRoot}' has not been read on this pod yet — a "
                + "reading is in flight; this probe answers only from readings already taken, "
                + "because taking one costs a walk of the volume and a probe has no budget for it",
            ],
            VolumeNotRead,
            _ => false);
}
