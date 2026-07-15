using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Read-only presence check for stream-routed participant addresses (a co-deployed language gate on
/// the trusted loopback endpoint, or a token-authenticated participant) connected to THIS process.
/// </summary>
/// <remarks>
/// Implemented by the gRPC connection registry and resolved via
/// <c>hub.ServiceProvider.GetService&lt;IParticipantPresence&gt;()</c> — it is <b>absent</b> when the gRPC
/// transport is not hosted (e.g. a pure test mesh), in which case callers fall back to the normal
/// request/<c>DeliveryFailure</c> path.
///
/// <para>Presence is scoped to the local process. In the co-hosted portal topology every silo pod also
/// runs the gates as sidecars on its own loopback, so a per-node hub activated on that silo sees the
/// gate that would serve it. This is used to <b>fail a foreign-language Code run fast</b> when no worker
/// is connected: a post to a stream-routed address with no subscriber is silently absorbed by the
/// Orleans memory stream (no <c>DeliveryFailure</c> is produced), so without this check the run's
/// ActivityLog would stay <c>Running</c> forever.</para>
/// </remarks>
public interface IParticipantPresence
{
    /// <summary>
    /// True when a participant is currently connected to this process and owns <paramref name="address"/>
    /// (i.e. a language worker is registered at that address and can service a delivery to it).
    /// </summary>
    bool IsConnected(Address address);
}
