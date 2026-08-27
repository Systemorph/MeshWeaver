using System.Reactive;

namespace MeshWeaver.Messaging;

/// <summary>
/// The hosting activation's own "I am completely gone" signal, registered on the hub
/// configuration by the Orleans grain during activation alongside
/// <see cref="GrainKeepAliveCallback"/>, <see cref="GrainLongRunningOperationCallback"/> and
/// <see cref="GrainDeactivateCallback"/>. Those three are things a straggler DOES to the
/// activation; this is the one thing the activation TELLS anyone waiting on it.
///
/// <para><see cref="Deactivated"/> fires <see cref="Unit"/> exactly once — and replays that
/// terminal to every later subscriber — when the activation has FULLY deactivated: Orleans has
/// run <c>OnDeactivateAsync</c>, stopped the grain lifecycle, unregistered the directory entry,
/// removed the activation from the silo catalog (<c>UnregisterMessageTarget</c>) and disposed it
/// (<c>State = Invalid</c>). In Orleans 10.2.2 that is precisely
/// <c>IGrainContext.Deactivated</c>, whose completion source is set on the last line of
/// <c>ActivationData.FinishDeactivating</c>, strictly after the catalog removal.</para>
///
/// <para>🚨 <b>Why this exists.</b> Nothing outside the silo could observe the END of a
/// deactivation, so anything that needed to wait for one SAMPLED the silo catalog on an interval
/// and raced a <c>Timeout</c> against it — the shape AGENTS.md and issue #2488 forbid, and the
/// direct cause of the recurring teardown crash in #2301: the timeout settled the waiting task
/// while a fresh batch of catalog queries was still in flight against a silo mid-teardown, and
/// each of those could then fault into nothing. A poll exists because a signal does not; this is
/// the signal.</para>
///
/// <para>In monolith mode no value is set — there is no activation to deactivate, so callers
/// treat the absence as "not grain-hosted" exactly as they do for the three callbacks above.</para>
/// </summary>
/// <param name="Deactivated">Fires <see cref="Unit"/> once and completes when the hosting
/// activation is fully gone; replays to late subscribers. Subscribe with an error arm — never
/// bridge it to a <see cref="System.Threading.Tasks.Task"/> except through
/// <see cref="ReactiveCompletion.ObserveCompletion{T}"/> at a genuine async edge.</param>
public record GrainDeactivationCompleted(IObservable<Unit> Deactivated);
