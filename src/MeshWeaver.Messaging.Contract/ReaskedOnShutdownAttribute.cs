namespace MeshWeaver.Messaging;

/// <summary>
/// Marks a request type whose protocol RE-ASKS on a transient <c>ShuttingDown</c> answer: every
/// producer of it observes the reply and, on the refusal an activation gives while it goes down,
/// sends the request again to the address's next activation.
///
/// <para>The marker is read in one place, the discard report of a hub that is disposed while
/// deliveries are still parked behind its initialization gates (<c>MessageService.Dispose</c>,
/// event 7301). Those deliveries cannot be served, because the gates only ever close for a
/// bring-up that the teardown ends. Each one is answered with a transient
/// <c>ShutdownNack.RetryForTheAuthoritativeAnswer</c>. For a marked request that answer loses
/// nothing: its sender re-asks, and the next activation serves it. The report is therefore
/// teardown-normal (Debug), not an Error. For any other request the waiter may take the NACK as
/// its final answer, so the discard stays an Error.</para>
///
/// <para>🚨 This is a claim about EVERY producer of the type, not about one of them. Attach it
/// only when all of them ride out a <c>ShuttingDown</c> answer. See
/// <c>Doc/Architecture/RidingOutAShuttingDownAddress</c>, and
/// <c>Doc/Architecture/TeardownLayers</c> → <i>Handler turns</i> for the carve-out it
/// implements.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
public sealed class ReaskedOnShutdownAttribute : Attribute
{
}
