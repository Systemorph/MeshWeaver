using System.Collections.Immutable;
using MeshWeaver.Data;

// The seam lives in the namespace of MessageHubConfiguration on purpose: every NodeType configuration
// lambda (src/ and in-mesh Source/*.cs alike) already sees MeshWeaver.Messaging, so the resident
// overload is found beside WithInitialization without an extra using — the next arm someone writes
// should not have to know a second namespace to get the guard.
namespace MeshWeaver.Messaging;

/// <summary>
/// The ONE seam for <b>resident</b> initialization: work a hub arms for its whole lifetime (a sweep,
/// a watcher, a timer, a write) that must never run on a hub carrying the
/// <see cref="TransientNodeProbe"/> marker.
///
/// <para><b>Why a seam.</b> The synchronous <c>WithInitialization(Action&lt;IMessageHub&gt;)</c> hooks
/// run inside <c>MessageHubConfiguration.Build</c>, and a transient node probe
/// (<c>content-type-registration/{guid}</c>, <c>$model-probe/{guid}</c>, <c>$schema-probe/{guid}</c>, …)
/// applies a NodeType's full instance configuration and is disposed in the same breath — so every such
/// hook ran on every probe. An always-on hub that armed a sweep there left an orphaned sweep (and its
/// half-written records) behind each probe, and a write issued from a probe died on
/// <c>HubDisposedBeforeResponseException</c> (Systemorph/MeshWeaver#6641). Guarding each arm by hand
/// means the next arm is unguarded by default; the check lives here, once.</para>
///
/// <para><b>Semantics.</b> On a resident hub the action runs exactly as a plain
/// <c>WithInitialization</c> would: synchronously inside <c>Build</c>, before message processing starts,
/// at its registration position among the other synchronous initializers. On a probe hub it never runs.
/// The probe marker is read from the BUILT hub's configuration, because the probe producers apply
/// <c>AsTransientNodeProbe()</c> after the NodeType's configuration — a check at registration time
/// would see no marker.</para>
///
/// <para><b>Idempotent on the caller's delegate</b>, like <c>WithInitialization</c>: the same delegate
/// registered twice (configurators composed through several layers) runs once. The guarding wrapper is
/// remembered per delegate on the configuration itself (an internal record keyed by the caller's delegate),
/// so re-registration hands <c>WithInitialization</c> the identical wrapper and its own identity check
/// collapses it — no static state.</para>
///
/// <para>Use plain <c>WithInitialization</c> for what a probe NEEDS to read (type registrations, data
/// context configuration); use this for everything that serves the node over time.</para>
/// </summary>
public static class ResidentInitializationExtensions
{
    /// <summary>
    /// Registers <paramref name="action"/> as a synchronous initializer that runs on a resident hub
    /// exactly as <c>WithInitialization</c> would, and never on a hub carrying the
    /// <see cref="TransientNodeProbe"/> marker.
    /// </summary>
    /// <param name="config">The hub configuration to extend.</param>
    /// <param name="action">The resident initialization — arming a sweep, a watcher, a write.</param>
    /// <returns>The configuration with the guarded initializer appended (unchanged when this delegate
    /// is already registered).</returns>
    public static MessageHubConfiguration WithResidentInitialization(
        this MessageHubConfiguration config, Action<IMessageHub> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var guards = config.Get<ResidentInitializationWrappers>()?.Guards
            ?? ImmutableDictionary<Action<IMessageHub>, Action<IMessageHub>>.Empty;
        if (guards.TryGetValue(action, out var existing))
            return config.WithInitialization(existing);

        Action<IMessageHub> guarded = hub =>
        {
            if (hub.IsTransientNodeProbe())
                return;
            action(hub);
        };
        return config
            .Set(new ResidentInitializationWrappers(guards.Add(action, guarded)))
            .WithInitialization(guarded);
    }

    /// <summary>
    /// True when <paramref name="hub"/> is a transient node probe: it carries the
    /// <see cref="TransientNodeProbe"/> marker, or sits at an address minted by
    /// <see cref="TransientProbeAddresses"/>. Either way it exists only to be read for type information
    /// and is disposed in the same breath. The one predicate the resident seam consults; prefer the seam
    /// over calling this from an initializer.
    /// </summary>
    /// <param name="hub">The hub to classify.</param>
    /// <returns>True for a transient node probe; false for every resident hub.</returns>
    public static bool IsTransientNodeProbe(this IMessageHub hub) =>
        hub.Configuration.Get<TransientNodeProbe>() is not null
        || TransientProbeAddresses.IsProbeAddress(hub.Address.ToString());
}

/// <summary>
/// Per-configuration memory of the guard wrapper minted for each resident initializer, so registering
/// the same delegate again yields the same wrapper and <c>WithInitialization</c>'s identity check keeps
/// it to one run. Carried on the configuration (an instance), never in a static.
/// </summary>
/// <param name="Guards">Caller delegate → the probe-guarding wrapper registered for it.</param>
internal sealed record ResidentInitializationWrappers(
    ImmutableDictionary<Action<IMessageHub>, Action<IMessageHub>> Guards);
