using System.Reactive.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Messaging;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// The platform self-update's answer to GitSync's "does this instance know of a newer platform?"
/// (<see cref="INewerPlatformReading"/>): the newest tag the poller recorded on
/// <c>Admin/UpdatePolicy</c>, when it is newer than the running build — whether the roll onto it is
/// available or HELD by a gate (a held instance is exactly the one that must not take sources nobody
/// stamped a floor for). Read through <see cref="PlatformUpdateStatus.Observe"/>, which reads as System,
/// bounds its own reads and degrades every failure to <see cref="PlatformUpdateStatus.Unknown"/> —
/// which answers null here, "not lagging".
/// </summary>
public sealed class NewerPlatformFromUpdatePolicy : INewerPlatformReading
{
    /// <inheritdoc />
    public IObservable<string?> NewerPlatform(IMessageHub hub)
        => PlatformUpdateStatus.Observe(hub)
            .Select(status => status.Availability is PlatformUpdateAvailability.UpdateAvailable
                                  or PlatformUpdateAvailability.UpdateHeld
                ? status.LatestVersion
                : null);
}
