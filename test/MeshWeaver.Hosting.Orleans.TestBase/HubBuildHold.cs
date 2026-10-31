using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Testing.FaultInjection;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The <b>hub-build hold</b> of one silo (#7470): while a switch from <see cref="Hold"/> is closed, a
/// per-node grain for that path on THIS silo builds nothing — its node resolution, NodeType binding and
/// hub construction wait behind the switch — so every delivery it accepts is parked on the unbuilt hub,
/// answered <c>Submitted</c>, exactly as during a slow cold build in production. Each parked delivery is
/// published on <see cref="Parked"/>, the positive signal a case waits on before it changes anything
/// else ("the grain has accepted the subscribe").
///
/// <para>Registered per silo by <see cref="FaultInjectionCluster"/> and reached through
/// <see cref="FaultInjectionCluster.HubBuilds"/>. Production never registers the seam it implements
/// (<c>IHubBuildSeam</c>), and the grain then builds exactly as it always did.</para>
/// </summary>
public sealed class HubBuildHold : IHubBuildSeam
{
    private ImmutableDictionary<string, FaultSwitch> _holds = ImmutableDictionary<string, FaultSwitch>.Empty;
    private readonly ReplaySubject<(string Path, IMessageDelivery Delivery)> _parked = new();

    /// <summary>The silo this hold belongs to, for failure messages.</summary>
    public string Silo { get; }

    /// <summary>Creates the hold for one silo.</summary>
    /// <param name="silo">The silo's name.</param>
    public HubBuildHold(string silo) => Silo = silo;

    /// <summary>
    /// From now on, a grain for <paramref name="path"/> on this silo holds its hub build until the
    /// returned switch is released. Each held build is an arrival on the switch. Release it in a
    /// <c>finally</c>.
    /// </summary>
    /// <param name="path">The node path (the grain key).</param>
    public FaultSwitch Hold(string path)
    {
        var hold = new FaultSwitch($"hold the hub build of '{path}' on {Silo}",
            released => ImmutableInterlocked.Update(ref _holds, holds =>
                holds.TryGetValue(path, out var current) && ReferenceEquals(current, released)
                    ? holds.Remove(path)
                    : holds));
        ImmutableInterlocked.AddOrUpdate(ref _holds, path, hold, (_, previous) =>
        {
            previous.Release();
            return hold;
        });
        return hold;
    }

    /// <summary>Every delivery a grain on this silo parked on its unbuilt hub, with the grain's path (replayed).</summary>
    public IObservable<(string Path, IMessageDelivery Delivery)> Parked => _parked.AsObservable();

    /// <inheritdoc />
    IObservable<MeshNode> IHubBuildSeam.Build(string path, IObservable<MeshNode> build)
        => _holds.TryGetValue(path, out var hold) ? hold.Gate(build, $"hub build of '{path}'") : build;

    /// <inheritdoc />
    void IHubBuildSeam.Parked(string path, IMessageDelivery delivery) => _parked.OnNext((path, delivery));
}
