namespace MeshWeaver.AI;

/// <summary>
/// Thrown by a co-hosted CLI chat client when the user's provider session needs (re)authentication
/// — deliberately distinct from transient/informational events such as the Claude Code SDK's
/// <c>rate_limit_event</c> (which is swallowed, not escalated). <c>ThreadExecution</c>'s error
/// branch turns this into a response-cell <c>Error</c> carrying an <c>action://connect</c> markdown
/// link, so the chat view can offer a "Connect / Log in" affordance that re-enters the Connect flow.
/// </summary>
public sealed class AuthRequiredException : Exception
{
    /// <summary>Provider key the user must (re)connect, e.g. <c>"ClaudeCode"</c> or <c>"Copilot"</c>.</summary>
    public string Provider { get; }

    public AuthRequiredException(string provider, string message, Exception? innerException = null)
        : base(message, innerException)
        => Provider = provider;

    /// <summary>The structured action URI the chat view detects to re-enter Connect for this provider.</summary>
    public string ActionLink => $"action://connect?provider={Uri.EscapeDataString(Provider)}";

    /// <summary>Markdown rendered on the response cell when this escalates (Status = Error).</summary>
    public string ToMarkdown() =>
        $"**Authentication required for {Provider}.** " +
        $"[Connect your {Provider} subscription]({ActionLink}) to continue.";
}
