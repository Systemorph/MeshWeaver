using System.Runtime.CompilerServices;
using System.Text;
using ClaudeAgentSdk;
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
    private readonly ILogger? logger;

    public ClaudeCodeChatClient(
        ClaudeCodeConfiguration configuration,
        string? modelName = null,
        ILogger<ClaudeCodeChatClient>? logger = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.modelName = modelName;
        this.logger = logger;
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
        var claudeOptions = new ClaudeAgentOptions
        {
            // Set UTF-8 encoding environment variables for proper character handling on Windows
            Env = new Dictionary<string, string>
            {
                ["PYTHONUTF8"] = "1",                    // Python UTF-8 mode
                ["PYTHONIOENCODING"] = "utf-8",          // Python I/O encoding
                ["LANG"] = "en_US.UTF-8",                // Unix locale
                ["LC_ALL"] = "en_US.UTF-8",              // Unix locale override
                ["CHCP"] = "65001"                       // Windows code page hint
            }
        };

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

        // Use the static QueryAsync method for streaming
        await foreach (var message in ClaudeAgent.QueryAsync(userPrompt, claudeOptions).WithCancellation(linkedCts.Token))
        {
            // Process different message types
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
                                    yield return new ChatResponseUpdate(ChatRole.Assistant, textBlock.Text);
                                }
                                break;

                            case ToolUseBlock toolUseBlock:
                                // Convert tool use to FunctionCallContent
                                var toolId = toolUseBlock.Id ?? Guid.NewGuid().ToString();
                                IDictionary<string, object?>? arguments = null;

                                if (toolUseBlock.Input != null && toolUseBlock.Input.Count > 0)
                                {
                                    // Input is already a Dictionary<string, object>, convert to nullable version
                                    arguments = toolUseBlock.Input.ToDictionary(
                                        kvp => kvp.Key,
                                        kvp => (object?)kvp.Value);
                                }

                                var functionCall = new FunctionCallContent(toolId, toolUseBlock.Name ?? "unknown", arguments);
                                yield return new ChatResponseUpdate(ChatRole.Assistant, [functionCall]);
                                break;

                            case ThinkingBlock thinkingBlock:
                                // Optionally expose thinking as a special content type or log it
                                logger?.LogDebug("Claude thinking: {Thinking}", thinkingBlock.Thinking);
                                break;
                        }
                    }
                    break;

                case SystemMessage systemMessage:
                    // System messages from the SDK (not the LLM)
                    logger?.LogDebug("Claude system message: {Subtype}", systemMessage.Subtype);
                    break;

                case ResultMessage resultMessage:
                    // Query completed - log cost/duration info
                    logger?.LogInformation(
                        "Claude query completed. Duration: {Duration}ms, Cost: ${Cost:F4}",
                        resultMessage.DurationMs,
                        resultMessage.TotalCostUsd ?? 0.0);
                    break;
            }
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
    private string BuildSystemPrompt(List<ChatMessage> messages)
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
