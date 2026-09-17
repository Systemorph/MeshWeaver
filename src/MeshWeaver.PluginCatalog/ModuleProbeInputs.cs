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
    Func<ModuleActivationEntry, bool> LandedDllExists);
