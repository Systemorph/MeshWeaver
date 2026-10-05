using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Subjects;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// 🚨 "This pod has begun TERMINATING" — known at the FIRST <c>/drain</c> probe, i.e. when preStop starts,
/// up to the whole termination grace period BEFORE SIGTERM fires <c>ApplicationStopping</c>
/// (see <c>DrainProgress</c>). A process-wide singleton, so the endpoint that learns it and the grains that
/// act on it share one answer.
///
/// <para><b>Why it exists (maintainer, 2026-10-04: "babysitter should be 1 process per cloud").</b> The
/// control instance's always-on singleton hub (<c>Hosting/PlatformBuilds</c>: the PR babysitter and the PR
/// steward's intake) sat on a DRAINING pod for up to 30 minutes after each roll — reviews and passes stalled
/// 09:00–09:37 — because nothing told its activation the pod was leaving until SIGTERM. A hub configured
/// with <see cref="HostDrainExtensions.RelocateOnDrain"/> now moves to a live silo the moment drain
/// begins.</para>
///
/// <para>Deliberately NOT folded into <c>IsLeaving()</c>: every other hub on a draining pod keeps serving
/// the sessions that hold the pod open (that is what the drain waits for). Only the opted-in singletons
/// move.</para>
/// </summary>
public sealed class HostDrainSignal : IDisposable
{
    private readonly ReplaySubject<Unit> began = new(1);
    private int begun;
    private ImmutableHashSet<string> relocating = ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    /// <summary>True once termination has begun on this process.</summary>
    public bool Begun => Volatile.Read(ref begun) != 0;

    /// <summary>Emits once when termination begins (replayed to later subscribers).</summary>
    public IObservable<Unit> WhenBegun => began;

    /// <summary>Marks termination as begun — idempotent; the first call emits.</summary>
    /// <returns>True when this call began it.</returns>
    public bool Begin()
    {
        if (Interlocked.Exchange(ref begun, 1) != 0)
            return false;
        began.OnNext(Unit.Default);
        began.OnCompleted();
        return true;
    }

    /// <summary>Registers an address whose activation must leave this process when drain begins.</summary>
    /// <param name="address">The hub's address.</param>
    public void Relocate(Address address) =>
        ImmutableInterlocked.Update(ref relocating, set => set.Add(address.ToString()));

    /// <summary>True when the hub at <paramref name="address"/> moves off this process on drain.</summary>
    /// <param name="address">The hub's address.</param>
    public bool Relocates(Address address) => Volatile.Read(ref relocating).Contains(address.ToString());

    /// <summary>True when drain has begun AND the hub at <paramref name="address"/> moves on drain.</summary>
    /// <param name="address">The hub's address.</param>
    public bool MustLeave(Address address) => Begun && Relocates(address);

    /// <inheritdoc />
    public void Dispose() => began.Dispose();
}

/// <summary>Configuration for hubs that are singletons of the whole cloud instance.</summary>
public static class HostDrainExtensions
{
    /// <summary>
    /// Marks this hub as an always-on SINGLETON of the instance: when its process begins to drain
    /// (<see cref="HostDrainSignal"/>), its activation is handed off to another live silo at once — never
    /// left to serve, or stall, from a pod that is being replaced. Monolith: a no-op (one process).
    /// </summary>
    /// <param name="config">The hub configuration.</param>
    public static MessageHubConfiguration RelocateOnDrain(this MessageHubConfiguration config) =>
        config.WithInitialization(hub =>
        {
            if (hub.ServiceProvider.GetService(typeof(HostDrainSignal)) is HostDrainSignal signal)
                signal.Relocate(hub.Address);
        });
}
