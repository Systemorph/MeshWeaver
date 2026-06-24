using System.Reflection;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI.Attributes;

/// <summary>
/// Marks a method exposed as an AI tool (via <c>AIFunctionFactory.Create</c>) as
/// <b>internal plumbing</b>: its calls are NOT surfaced in the chat UI as tool-call
/// chrome ("Calling …") and are NOT logged as agent tool activity. The tool still
/// runs and its result is still fed back to the model — only the user-facing /
/// operator-facing surfacing is suppressed.
///
/// <para>Canonical use: <c>check_inbox</c>, the mid-round inbox poll the agent fires
/// between steps. It is a high-frequency internal poll, not a user action, so showing
/// "Calling check_inbox…" on every step is pure noise. Read at tool-wrap time the same
/// way <see cref="ToolTimeoutAttribute"/> is (via the inner function's underlying
/// method) — see <c>AgentChatClient</c> for the forwarding filter.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class HiddenToolAttribute : Attribute
{
    /// <summary>
    /// True when <paramref name="function"/>'s underlying method carries
    /// <see cref="HiddenToolAttribute"/> — i.e. its calls must be hidden from the chat
    /// UI and tool-activity logs. <see cref="DelegatingAIFunction"/> wrappers (e.g. the
    /// access-context wrapper) forward <see cref="AIFunction.UnderlyingMethod"/>, so the
    /// marker is visible through them too.
    /// </summary>
    public static bool IsHidden(AIFunction function) =>
        function.UnderlyingMethod?.GetCustomAttribute<HiddenToolAttribute>() is not null;
}
