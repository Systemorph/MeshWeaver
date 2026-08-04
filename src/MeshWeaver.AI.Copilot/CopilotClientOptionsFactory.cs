using GitHub.Copilot;

namespace MeshWeaver.AI.Copilot;

/// <summary>
/// Builds <see cref="CopilotClientOptions"/> from our configuration shape.
/// </summary>
/// <remarks>
/// SDK 1.0.8 replaced the flat <c>AutoStart</c> / <c>CliPath</c> / <c>CliUrl</c> / <c>Port</c>
/// switches with a single <c>Connection</c> of type <c>RuntimeConnection</c>, and renamed
/// <c>CopilotHome</c> to <c>BaseDirectory</c>. The three transports are mutually exclusive, which the
/// old flat shape could not express — setting both <c>CliUrl</c> and <c>Port</c> was silently
/// ambiguous. Centralised here so the connect strategy and the chat client cannot drift apart on
/// which one wins.
/// </remarks>
internal static class CopilotClientOptionsFactory
{
    /// <summary>
    /// Precedence: an explicit URL, else an explicit port, else the configured CLI executable.
    /// <para>Returns <c>null</c> when none is configured — and null is MEANINGFUL: the SDK then
    /// spawns its bundled runtime over stdio, which is exactly what the old
    /// <c>AutoStart = true</c> with no <c>CliPath</c> did. Do not "fix" this by substituting a
    /// hard-coded executable name.</para>
    /// </summary>
    public static RuntimeConnection? CreateConnection(string? cliPath, string? cliUrl, int? port) =>
        !string.IsNullOrEmpty(cliUrl) ? RuntimeConnection.ForUri(cliUrl)
        : port.HasValue ? RuntimeConnection.ForTcp(port.Value)
        : !string.IsNullOrEmpty(cliPath) ? RuntimeConnection.ForStdio(cliPath)
        : null;

    /// <summary>
    /// <paramref name="copilotHome"/> maps to <c>BaseDirectory</c>, which the SDK turns into
    /// <c>COPILOT_HOME</c> on the spawned runtime. It is IGNORED when connecting to an already
    /// running runtime (URL / TCP) — per-user config dirs only mean something for a CLI we spawn.
    /// </summary>
    public static CopilotClientOptions Create(
        string? cliPath, string? cliUrl, int? port, string? copilotHome = null)
    {
        var options = new CopilotClientOptions();
        // Leave Connection unset when nothing is configured — null means "bundled runtime over
        // stdio", the SDK's own default.
        if (CreateConnection(cliPath, cliUrl, port) is { } connection)
            options.Connection = connection;
        if (!string.IsNullOrEmpty(copilotHome))
            options.BaseDirectory = copilotHome;
        return options;
    }
}
