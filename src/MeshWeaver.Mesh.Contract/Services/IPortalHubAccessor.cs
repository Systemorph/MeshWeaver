using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Hands out the PORTAL's hub — the per-circuit hub a signed-in user's UI work runs on — to code
/// that must not reference the portal assembly to get it.
///
/// <para>🚨 The distinction this exists to preserve: <b>the portal hub is not an ambient
/// <see cref="IMessageHub"/></b>. Resolving whatever hub happens to be in scope gets the mesh hub
/// (the ROUTER, which must not execute work) or a per-node hub, and the difference only shows up as
/// misrouted traffic at runtime. Callers that need the portal's own hub have to say so.</para>
///
/// <para><see cref="Hub"/> is NULLABLE by design: a headless host, a test fixture, or a deployment
/// with no GUI has no portal hub, and that is not an error. Consumers degrade — the AI skill sync
/// writes its base instructions and logs that the rest is reachable another way — rather than
/// failing a boot over an absent UI.</para>
/// </summary>
public interface IPortalHubAccessor
{
    /// <summary>The portal hub for the current scope, or <c>null</c> where there is no portal.</summary>
    IMessageHub? Hub { get; }
}
