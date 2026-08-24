namespace MeshWeaver.GitSync;

/// <summary>
/// Configuration for the <see cref="FrameworkReleaseBroadcaster"/>, bound from the
/// <c>FrameworkBroadcast</c> configuration section. This is the <b>interim</b> source of the
/// subscriber set: the durable home is the Hosting fleet's subscriber registry on the control
/// instance (a repo registers there — "set up a new repo in Hosting"), and the Hosting watcher
/// passes that list straight to <see cref="FrameworkReleaseBroadcaster.Broadcast"/>. When no list
/// is passed, the broadcaster falls back to <see cref="Subscribers"/> here so a mesh can be wired
/// from config alone until the registry is populated.
/// </summary>
public sealed record FrameworkBroadcastOptions
{
    /// <summary>
    /// The subscriber repositories, each <c>owner/name</c> (a leading
    /// <c>https://github.com/</c> and a trailing <c>.git</c> are tolerated and stripped). Only the
    /// control instance — the one memex that holds the GitHub App — should carry a non-empty list;
    /// everywhere else it stays empty and the broadcaster is inert.
    /// </summary>
    public string[] Subscribers { get; init; } = [];

    /// <summary>
    /// The <c>repository_dispatch</c> event type the satellites subscribe to
    /// (<c>on: repository_dispatch: { types: [meshweaver-framework-released] }</c>). Overridable
    /// only for tests / a future second wave; the default is the one every satellite listens for.
    /// </summary>
    public string EventType { get; init; } = FrameworkReleaseBroadcaster.DefaultEventType;
}
