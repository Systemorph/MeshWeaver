namespace MeshWeaver.Messaging;

/// <summary>
/// Marks a message type as framework-internal infrastructure traffic that
/// carries no security-relevant payload. The PostPipeline suppresses its
/// "posted with no AccessContext" warning for messages bearing this
/// attribute — these are heartbeats, hub-lifecycle requests, subscription
/// management messages, etc. that the framework itself emits and that
/// downstream handlers don't need a user identity to process.
///
/// <para><b>Use sparingly.</b> Marking an application message with
/// <see cref="SystemMessageAttribute"/> bypasses the safety net that
/// catches "this Post lost the user's identity" bugs. Only attach it to
/// genuinely identity-free framework traffic. Responses propagate identity
/// via <see cref="PostOptions.ResponseFor"/> automatically and should NOT
/// be marked — let the framework wire identity through them.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
public class SystemMessageAttribute : Attribute
{
}
