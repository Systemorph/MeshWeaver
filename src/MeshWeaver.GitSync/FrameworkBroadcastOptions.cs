namespace MeshWeaver.GitSync;

/// <summary>
/// Configuration for the <see cref="FrameworkReleaseBroadcaster"/>, bound from the
/// <see cref="ConfigSection"/> configuration section.
///
/// <para>🚨 <b>This is the ONLY source of the subscriber set that exists</b> (#2235). Both
/// <c>main-cd.yml</c> and the first draft of this file described it as an "interim" fallback whose
/// durable home was "the Hosting fleet registry on the control instance" — that registry was never
/// built (there is no subscriber node type in <c>Hosting/</c> and no code anywhere reads one), and
/// naming a mechanism that does not exist is what stopped four separate investigations from looking
/// at the seam that does. Until a registry is actually built and its reader ships, a repo is
/// subscribed by being listed here and by nothing else.</para>
/// </summary>
public sealed record FrameworkBroadcastOptions
{
    /// <summary>The configuration section this options record binds from.</summary>
    public const string ConfigSection = "FrameworkBroadcast";

    /// <summary>
    /// The environment-variable form of the first <see cref="Subscribers"/> slot
    /// (<c>FrameworkBroadcast__Subscribers__0</c>) — the exact name a deployment sets and the
    /// chart renders. Named here because a key the code reads and no chart renders cannot be set
    /// by any deploy, and reads as "deliberately off" rather than as missing.
    /// </summary>
    public const string SubscribersEnvKeyPrefix = "FrameworkBroadcast__Subscribers__";

    /// <summary>
    /// The webhook-inbox target that carries platform-build facts. An instance whose
    /// <c>WebhookInbox:Targets</c> allowlist contains this path IS the control instance: it
    /// receives release events and is therefore the one instance for which an empty
    /// <see cref="Subscribers"/> list is a misconfiguration rather than the normal state.
    /// </summary>
    public const string PlatformBuildsTarget = "Hosting/PlatformBuilds";

    /// <summary>
    /// The subscriber repositories, each <c>owner/name</c> (a leading
    /// <c>https://github.com/</c> and a trailing <c>.git</c> are tolerated and stripped). Only the
    /// control instance — the one memex that holds the GitHub App and receives platform-build
    /// deliveries — should carry a non-empty list; everywhere else it stays empty and the
    /// broadcaster is inert.
    /// </summary>
    public string[] Subscribers { get; init; } = [];

    /// <summary>
    /// The <c>repository_dispatch</c> event type the satellites subscribe to
    /// (<c>on: repository_dispatch: { types: [meshweaver-framework-released] }</c>). Overridable
    /// only for tests / a future second wave; the default is the one every satellite listens for.
    /// </summary>
    public string EventType { get; init; } = FrameworkReleaseBroadcaster.DefaultEventType;
}
