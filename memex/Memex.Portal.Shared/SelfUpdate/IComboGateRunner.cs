using MeshWeaver.GitSync;
using MeshWeaver.PluginCatalog;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// 🚨 <b>The off-pod half of the combo gate: everything
/// <see cref="InstanceComboVerifier"/> needs that a portal POD does not have.</b>
///
/// <para>Producing a combo verdict means materialising every module of this instance's combo at its
/// recorded ref and executing <c>mw-plugin-test</c> INSIDE the candidate image. That needs docker, a
/// writable work root, and read access to the module repositories — none of which a portal pod has,
/// which is why <c>mw-combo-verify</c> lives in <c>tools/</c> and the Candidate Release Protocol runs
/// it off-cluster. This interface is that host's contribution, expressed as ONE optional service so
/// the gate stays the same code whether it produces its own verdict or reads the one an operator (or
/// CD) landed.</para>
///
/// <para><b>Its absence is never a pass.</b> A host with no runner registered simply produces no
/// verdict — <see cref="ComboVerificationGate"/> then CONSULTS what is recorded on
/// <c>Admin/UpdatePolicy</c>, and a candidate with no verdict resolves to
/// <see cref="ComboClearanceKind.NotVerified"/>, which grants nothing. Only a
/// <see cref="ComboVerdictKind.Green"/> verdict can ever clear a candidate.</para>
///
/// <para>Reactive: the fetch and the gate run are cold observables that do their work on Subscribe,
/// never <c>Task</c>-shaped — a hub-reachable caller must not await anything.</para>
/// </summary>
public interface IComboGateRunner
{
    /// <summary>The directory the combo is materialised into and the gate runs over. Must be
    /// writable by this process.</summary>
    string WorkRoot { get; }

    /// <summary>
    /// Executes <c>mw-plugin-test &lt;root&gt; --report …</c> inside the candidate image:
    /// (imageRef, workRoot) → one <see cref="CandidateGateRun"/>. Cold. Expected failures (docker
    /// missing, pull denied, timeout) are reported through <see cref="CandidateGateRun.Error"/>
    /// rather than as an OnError — the verifier folds either into <c>NotVerifiable</c>.
    /// </summary>
    IObservable<CandidateGateRun> Run(string imageRef, string workRoot);

    /// <summary>
    /// The snapshot fetch the assembler materialises modules through:
    /// (repositoryUrl, gitRef, subdirectory, accessToken) → one <see cref="RepoSnapshot"/> — the
    /// same shape <c>IGitHubRepoClient.Fetch</c> has, so a host wires its existing client here
    /// instead of hand-rolling git.
    /// </summary>
    IObservable<RepoSnapshot> Fetch(
        string repositoryUrl, string gitRef, string? subdirectory, string accessToken);

    /// <summary>Assembly policy for the run — the repo access token, the source→repository map,
    /// and whether moving refs may be materialised. Defaults REFUSE moving refs and incomplete
    /// combos, because a gate run on either is not evidence.</summary>
    ComboAssemblyOptions Options { get; }
}
