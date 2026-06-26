namespace MeshWeaver.AI;

/// <summary>
/// Thrown by a co-hosted CLI chat client when the user's provider session needs (re)authentication
/// — deliberately distinct from transient/informational events such as the Claude Code SDK's
/// <c>rate_limit_event</c> (which is swallowed, not escalated). <c>ThreadExecution</c>'s error
/// branch turns this into a response-cell <c>Error</c> whose markdown points the user at the harness's
/// own <c>/login</c> command (the chat surfaces the inline Connect flow), instead of a cryptic CLI
/// "exit code 1".
/// </summary>
public sealed class AuthRequiredException : Exception
{
    /// <summary>Provider key the user must (re)connect, e.g. <c>"ClaudeCode"</c> or <c>"Copilot"</c>.</summary>
    public string Provider { get; }

    /// <summary>
    /// Creates an authentication-required exception for the given provider.
    /// </summary>
    /// <param name="provider">Provider key the user must (re)connect, e.g. <c>"ClaudeCode"</c> or <c>"Copilot"</c>.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public AuthRequiredException(string provider, string message, Exception? innerException = null)
        : base(message, innerException)
        => Provider = provider;

    /// <summary>
    /// Markdown rendered on the response cell when this escalates (Status = Error). Points the user at
    /// the harness's <c>/login</c> command — typing it surfaces the inline Connect (login) flow.
    /// </summary>
    public string ToMarkdown() =>
        $"**Not logged in.** Type `/login` to connect your {Provider} subscription, then send your message again.";
}
