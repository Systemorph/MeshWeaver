using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text;
using ClaudeAgentSdk;
using MeshWeaver.AI.Connect;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.ClaudeCode;

/// <summary>
/// IChatClient implementation that bridges the Claude Agent SDK to Microsoft.Extensions.AI.
/// Uses the Claude Code CLI for agentic AI interactions.
/// </summary>
public class ClaudeCodeChatClient : IChatClient
{
    private readonly ClaudeCodeConfiguration configuration;
    private readonly string? modelName;
    private readonly string? configDir;
    private readonly string? oauthToken;
    private readonly IMcpBackConnection? mcpBackConnection;
    private readonly string? userId;
    private readonly string? userName;
    private readonly string? userEmail;
    private readonly ILogger? logger;

    public ClaudeCodeChatClient(
        ClaudeCodeConfiguration configuration,
        string? modelName = null,
        ILogger<ClaudeCodeChatClient>? logger = null,
        string? configDir = null,
        string? oauthToken = null,
        IMcpBackConnection? mcpBackConnection = null,
        string? userId = null,
        string? userName = null,
        string? userEmail = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.modelName = modelName;
        this.logger = logger;
        // Per-user isolation for co-hosted multi-user (Phase 5b): each spawn
        // gets the calling user's own .claude (CLAUDE_CONFIG_DIR) + subscription
        // token (CLAUDE_CODE_OAUTH_TOKEN), so concurrent users on one portal
        // replica never share credentials or session state.
        this.configDir = configDir;
        this.oauthToken = oauthToken;
        // Automatic MCP back-connection (the mesh is the CLI's workspace) — resolved per spawn.
        this.mcpBackConnection = mcpBackConnection;
        this.userId = userId;
        this.userName = userName;
        this.userEmail = userEmail;
    }

    /// <inheritdoc />
    public ChatClientMetadata Metadata => new("ClaudeCodeChatClient", providerUri: null, modelName);

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var contents = new List<AIContent>();
        var allText = new StringBuilder();

        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (update.Text != null)
            {
                allText.Append(update.Text);
            }

            foreach (var content in update.Contents)
            {
                if (content is FunctionCallContent functionCall)
                {
                    contents.Add(functionCall);
                }
            }
        }

        if (allText.Length > 0)
        {
            contents.Insert(0, new TextContent(allText.ToString()));
        }

        if (contents.Count == 0)
        {
            contents.Add(new TextContent(string.Empty));
        }

        var chatMessage = new ChatMessage(ChatRole.Assistant, contents);
        return new ChatResponse(chatMessage);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Build the prompt from messages
        var messageList = messages.ToList();
        var userPrompt = BuildPromptFromMessages(messageList);

        // Co-hosted multi-user: when this user runs under their OWN per-user config dir, a missing
        // subscription token AND missing .credentials.json means they've never logged in. Surface an
        // actionable auth error (ThreadExecution turns it into a "/login" affordance) instead of
        // letting the CLI fail later with a cryptic "Not logged in · Please run /login" →
        // ProcessException exit 1. (configDir is null only in single-user dev, where the CLI uses the
        // machine's own login — so we don't pre-empt there.)
        if (!string.IsNullOrEmpty(configDir) && string.IsNullOrEmpty(oauthToken) && !HasCredentials(configDir))
        {
            throw new AuthRequiredException(
                Harnesses.ClaudeCode,
                "Not logged in to Claude Code. Run /login to connect your Claude subscription.");
        }

        // If CliDirectory is specified, add it to PATH for CLI discovery
        // This must be done BEFORE calling the SDK as FindCli() checks PATH
        if (!string.IsNullOrEmpty(configuration.CliDirectory))
        {
            // Expand environment variables like %APPDATA% and normalize path separators
            var expandedPath = Environment.ExpandEnvironmentVariables(configuration.CliDirectory);
            // Normalize path separators for the current platform
            expandedPath = Path.GetFullPath(expandedPath);
            logger?.LogDebug("ClaudeCode CliDirectory: configured={Configured}, expanded={Expanded}",
                configuration.CliDirectory, expandedPath);
            EnsureCliInPath(expandedPath);
        }

        // Build options
        var env = new Dictionary<string, string>
        {
            ["PYTHONUTF8"] = "1",                    // Python UTF-8 mode
            ["PYTHONIOENCODING"] = "utf-8",          // Python I/O encoding
            ["LANG"] = "en_US.UTF-8",                // Unix locale
            ["LC_ALL"] = "en_US.UTF-8",              // Unix locale override
            ["CHCP"] = "65001"                       // Windows code page hint
        };
        // Per-user isolation: run the CLI under this user's own .claude + token.
        if (!string.IsNullOrEmpty(configDir))
        {
            env["CLAUDE_CONFIG_DIR"] = configDir;
            try { Directory.CreateDirectory(configDir); }
            catch (Exception ex) { logger?.LogWarning(ex, "Could not create CLAUDE_CONFIG_DIR {Dir}", configDir); }
        }
        if (!string.IsNullOrEmpty(oauthToken))
            env["CLAUDE_CODE_OAUTH_TOKEN"] = oauthToken;

        var claudeOptions = new ClaudeAgentOptions { Env = env };

        // Automatic MCP back-connection — the mesh is this CLI's workspace (no file tree). Inject a
        // per-spawn HTTP MCP server at the portal's /mcp, authenticated AS THE USER via a Bearer
        // token (minted/reused on demand by IMcpBackConnection). In-memory only — the token is
        // never written to the config dir / Azure Files share.
        if (mcpBackConnection != null && !string.IsNullOrEmpty(userId))
        {
            McpConnectionInfo? mcpInfo = null;
            try
            {
                mcpInfo = await mcpBackConnection.EnsureForUser(userId, userName, userEmail)
                    .FirstOrDefaultAsync().ToTask(cancellationToken);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not provision the MCP back-connection; Claude Code will run without mesh access.");
            }
            if (mcpInfo != null)
            {
                claudeOptions.McpServers = new Dictionary<string, McpHttpServerConfig>
                {
                    ["meshweaver"] = new McpHttpServerConfig
                    {
                        Type = "http",
                        Url = mcpInfo.McpUrl,
                        Headers = new Dictionary<string, string>
                        {
                            ["Authorization"] = $"Bearer {mcpInfo.BearerToken}"
                        }
                    }
                };
                logger?.LogInformation("Claude Code MCP workspace wired to {McpUrl}", mcpInfo.McpUrl);
            }
        }

        if (!string.IsNullOrEmpty(modelName))
        {
            claudeOptions.Model = modelName;
        }

        // Build system prompt: combine agent instructions (from system messages) with configuration
        var systemPrompt = BuildSystemPrompt(messageList);
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            claudeOptions.SystemPrompt = systemPrompt;
        }

        if (configuration.MaxTurns.HasValue)
        {
            claudeOptions.MaxTurns = configuration.MaxTurns.Value;
        }

        if (!string.IsNullOrEmpty(configuration.WorkingDirectory))
        {
            claudeOptions.Cwd = configuration.WorkingDirectory;
        }

        logger?.LogInformation("Starting Claude Code query with model {Model}, prompt length: {PromptLength}",
            modelName, userPrompt?.Length ?? 0);
        logger?.LogDebug("Claude Code prompt: {Prompt}", userPrompt);

        // Validate prompt is not empty
        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            logger?.LogWarning("Empty prompt received for Claude Code query");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "No message content received.");
            yield break;
        }

        // Set up timeout
        using var timeoutCts = new CancellationTokenSource(configuration.SessionTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // Manually enumerate so we survive the Claude Agent SDK 0.2.1 bug where
        // MessageParser throws ArgumentException("Unknown message type: …") on the
        // newer CLI's informational events (e.g. rate_limit_event) — which would
        // otherwise crash the whole chat. 0.2.1 is the LATEST SDK, so this wrapper
        // is the only fix until Anthropic ships one (claude-agent-sdk #583/#599/
        // #601/#603, claude-code #26498). The inner try/catch wraps MoveNextAsync
        // only (no yield inside it); the outer try/finally allows yield.
        var enumerator = ClaudeAgent.QueryAsync(userPrompt, claudeOptions)
            .GetAsyncEnumerator(linkedCts.Token);
        var yieldedAny = false;
        var swallowedUnknown = false;
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                }
                catch (ArgumentException ex) when (
                    ex.Message.Contains("Unknown message type", StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogWarning(ex,
                        "Claude Agent SDK could not parse a CLI message — ending stream gracefully (SDK 0.2.1 bug, e.g. rate_limit_event).");
                    swallowedUnknown = true;
                    break;
                }
                if (!moved) break;

                var message = enumerator.Current;
                switch (message)
                {
                    case AssistantMessage assistantMessage:
                        foreach (var block in assistantMessage.Content)
                        {
                            switch (block)
                            {
                                case TextBlock textBlock:
                                    if (!string.IsNullOrEmpty(textBlock.Text))
                                    {
                                        yieldedAny = true;
                                        yield return new ChatResponseUpdate(ChatRole.Assistant, textBlock.Text);
                                    }
                                    break;

                                case ToolUseBlock toolUseBlock:
                                    var toolId = toolUseBlock.Id ?? Guid.NewGuid().ToString();
                                    IDictionary<string, object?>? arguments = null;
                                    if (toolUseBlock.Input != null && toolUseBlock.Input.Count > 0)
                                    {
                                        arguments = toolUseBlock.Input.ToDictionary(
                                            kvp => kvp.Key, kvp => (object?)kvp.Value);
                                    }
                                    yieldedAny = true;
                                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                                        [new FunctionCallContent(toolId, toolUseBlock.Name ?? "unknown", arguments)]);
                                    break;

                                case ThinkingBlock thinkingBlock:
                                    logger?.LogDebug("Claude thinking: {Thinking}", thinkingBlock.Thinking);
                                    break;
                            }
                        }
                        break;

                    case SystemMessage systemMessage:
                        logger?.LogDebug("Claude system message: {Subtype}", systemMessage.Subtype);
                        break;

                    case ResultMessage resultMessage:
                        logger?.LogInformation(
                            "Claude query completed. Duration: {Duration}ms, Cost: ${Cost:F4}",
                            resultMessage.DurationMs, resultMessage.TotalCostUsd ?? 0.0);
                        break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        // If the only thing that stopped us was the unparseable event and we never
        // produced output, surface a graceful note instead of a silent empty reply.
        if (swallowedUnknown && !yieldedAny)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                "Claude Code produced no output before the session ended (often a transient rate limit). Please retry shortly.");
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(IChatClient))
            return this;
        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // No resources to dispose - each query creates its own connection
    }

    /// <summary>
    /// Builds the system prompt by combining agent instructions (from system messages in the list)
    /// with the global configuration system prompt.
    /// ChatClientAgent passes its instructions as a system message in the messages collection.
    /// </summary>
    internal string BuildSystemPrompt(List<ChatMessage> messages)
    {
        var parts = new List<string>();

        // Extract system messages (agent instructions from ChatClientAgent)
        foreach (var msg in messages.Where(m => m.Role == ChatRole.System))
        {
            var text = GetTextContent(msg);
            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(text);
            }
        }

        // Add global configuration system prompt
        if (!string.IsNullOrEmpty(configuration.SystemPrompt))
        {
            parts.Add(configuration.SystemPrompt);
        }

        return string.Join("\n\n", parts);
    }

    private static string BuildPromptFromMessages(List<ChatMessage> messages)
    {
        // For Claude Code, we typically just use the last user message as the prompt
        // The conversation history could be included in the system prompt if needed
        var lastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User);

        if (lastUserMessage != null)
        {
            return GetTextContent(lastUserMessage);
        }

        // If no user message, concatenate all messages
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            var role = message.Role.Value;
            var text = GetTextContent(message);
            if (!string.IsNullOrEmpty(text))
            {
                sb.AppendLine($"{role}: {text}");
            }
        }
        return sb.ToString().Trim();
    }

    private static string GetTextContent(ChatMessage message)
    {
        // First try the Text property (set by simple string constructor)
        if (!string.IsNullOrEmpty(message.Text))
            return message.Text;

        // Fallback to Contents collection (for messages with explicit content items)
        if (message.Contents.Count == 0)
            return string.Empty;

        var textParts = message.Contents
            .OfType<TextContent>()
            .Select(c => c.Text);

        return string.Join("", textParts);
    }

    /// <summary>
    /// True when the per-user config dir holds a non-empty <c>.credentials.json</c> (the CLI's
    /// persisted OAuth login). A cheap file probe that mirrors
    /// <c>ClaudeConnectStrategy.IsLoggedIn</c> — the Connect flow writes this file into the same dir.
    /// </summary>
    private static bool HasCredentials(string configDir)
    {
        try
        {
            var creds = Path.Combine(configDir, ".credentials.json");
            return File.Exists(creds) && new FileInfo(creds).Length > 2;
        }
        catch
        {
            return false;
        }
    }

    private static readonly object PathLock = new();
    private static bool _pathModified;

    /// <summary>
    /// Ensures the CLI directory is in the process's PATH environment variable.
    /// This is needed because the SDK's FindCli() checks PATH before we can set options.
    /// </summary>
    private static void EnsureCliInPath(string cliDirectory)
    {
        lock (PathLock)
        {
            if (_pathModified)
                return;

            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!currentPath.Contains(cliDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var newPath = $"{cliDirectory}{Path.PathSeparator}{currentPath}";
                Environment.SetEnvironmentVariable("PATH", newPath);
            }
            _pathModified = true;
        }
    }
}
