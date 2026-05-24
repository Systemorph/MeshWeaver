namespace MeshWeaver.AI.Attributes;

/// <summary>
/// Per-tool execution timeout. Applied to a method exposed as an AI tool
/// (via <c>AIFunctionFactory.Create</c>). Read at wrap time by
/// <c>AccessContextAIFunction</c> and enforced via a linked
/// <see cref="CancellationTokenSource"/>; on expiry the tool invocation
/// is cancelled and the agent receives a synthetic "timed out" string
/// instead of a hung promise.
///
/// <para>The framework default is 30 seconds — long enough for any
/// reasonable tool (mesh read, web search, single-shot LLM completion)
/// to complete, short enough that a hang surfaces fast in the chat UI.
/// Override on tools that legitimately take longer (long-running script
/// execution, large export, compile loop).</para>
///
/// <para>Does NOT apply to <c>delegate_to_agent</c> — that's a thread-
/// execution primitive with its own heartbeat-based hang detection
/// (see <c>MeshThread.LastActivityAt</c>), not a tool in the
/// timeout-attribute sense.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolTimeoutAttribute(int seconds) : Attribute
{
    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(seconds);
}
