namespace MeshWeaver.GitSync;

/// <summary>
/// Configuration for the <see cref="FrameworkReleaseBroadcaster"/>, bound from the
/// <see cref="ConfigSection"/> configuration section.
///
/// <para>🚨 <b>The subscriber set is NOT configuration.</b> It is data in the mesh: every repository
/// a <c>Hosting/Deployment</c> record on the control instance serves as a registry source
/// (<c>pluginRepos[].isRegistrySource</c>), derived at broadcast time by the Hosting module's
/// <c>PlatformBuildInboxWatcher</c> (MeshWeaver.Plugins) and passed to
/// <see cref="FrameworkReleaseBroadcaster.Broadcast"/>. The record that makes a repository part of
/// the fleet is the record that subscribes it, so the two cannot drift. This record used to carry a
/// <c>Subscribers</c> list (<c>FrameworkBroadcast__Subscribers__0..N</c>) as the "interim" source;
/// it was a hand-maintained second copy of that graph, rendered blank on the control instance for a
/// week (Memex#140) while nothing was red — retired 2026-09-03, together with core's own
/// GitHub→GitHub dispatcher (maintainer: <i>"memex issues an event that something has a new version;
/// GitHub subscribes to this and triggers the build. Core publishes an event and finishes."</i>).</para>
/// </summary>
public sealed record FrameworkBroadcastOptions
{
    /// <summary>The configuration section this options record binds from.</summary>
    public const string ConfigSection = "FrameworkBroadcast";

    /// <summary>
    /// The webhook-inbox target that carries platform-build facts. An instance whose
    /// <c>WebhookInbox:Targets</c> allowlist contains this path IS the control instance: it
    /// receives release events and is therefore the one instance for which an empty subscriber set
    /// is a misconfiguration (no Deployment record names a registry source) rather than the normal
    /// state.
    /// </summary>
    public const string PlatformBuildsTarget = "Hosting/PlatformBuilds";

    /// <summary>
    /// The <c>repository_dispatch</c> event type the satellites subscribe to
    /// (<c>on: repository_dispatch: { types: [meshweaver-framework-released] }</c>). Overridable
    /// only for tests / a future second wave; the default is the one every satellite listens for.
    /// </summary>
    public string EventType { get; init; } = FrameworkReleaseBroadcaster.DefaultEventType;
}
