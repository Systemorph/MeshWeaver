namespace MeshWeaver.Messaging;

/// <summary>
/// Marks a message type that only the mesh's OWN hubs may post — persistence, storage, hub
/// lifecycle and synchronisation plumbing whose handler trusts the message instead of checking
/// the caller. A delivery that entered the mesh from a remote participant (a SignalR or gRPC
/// connection, public or trusted endpoint — see <see cref="ParticipantIngress"/>) is refused
/// before any handler sees it, whoever it claims to come from.
///
/// <para>🚨 Every ingress forwards any delivery to any address, and the client writes the whole
/// envelope — the <c>Sender</c> included. So a handler cannot tell "my own hub posted this" from
/// "a client posted this and wrote my address as the sender" by looking at the delivery. The
/// ingress stamps <see cref="ParticipantIngress.Property"/> on everything it accepts, the stamp is
/// set AFTER the client's envelope is read (so a client cannot omit it), and the receiving hub
/// refuses a stamped delivery of a type carrying this attribute once the message is typed —
/// which is the only point where a type that crossed the wire as raw JSON can be recognised.</para>
///
/// <para>Use it for messages whose handler writes or reads storage raw, drives a compile from a
/// payload, or manipulates a hub's or stream's internal state. Do NOT use it to "secure" an
/// application request: those carry <c>[RequiresPermission]</c> and are checked under the
/// caller's own identity, which is the right answer for anything a client may legitimately
/// send. The inventory lives in <c>Doc/Architecture/ParticipantIngress</c>.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
public sealed class InfrastructureOnlyAttribute : Attribute
{
}
