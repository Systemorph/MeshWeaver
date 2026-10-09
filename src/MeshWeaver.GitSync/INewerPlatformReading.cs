using MeshWeaver.Messaging;

namespace MeshWeaver.GitSync;

/// <summary>
/// 🚨 What a GitSync import consults before it writes sources whose platform floor is NOT verified
/// for them (<see cref="ModuleSyncDecision.HoldUnverifiedFloors"/>): does THIS instance know of a
/// platform build newer than the one it runs? A lagging instance does not take sources nobody has
/// stamped a floor for; an instance on the newest platform it knows of takes every push.
///
/// <para>Implemented by the platform self-update (<c>Memex.Portal.Shared</c>, which records the newest
/// tag it has seen on <c>Admin/UpdatePolicy</c>); a mesh that registers none knows of no newer
/// platform, and the rule abstains — exactly the behaviour before it existed.</para>
/// </summary>
public interface INewerPlatformReading
{
    /// <summary>
    /// The newest platform version this instance knows is available and that is NEWER than the one
    /// it runs, or null when it runs the newest it knows of — or cannot tell, which the rule reads as
    /// "not lagging". Emits once.
    /// </summary>
    /// <param name="hub">The hub the import runs on — the reading routes from it.</param>
    IObservable<string?> NewerPlatform(IMessageHub hub);
}
