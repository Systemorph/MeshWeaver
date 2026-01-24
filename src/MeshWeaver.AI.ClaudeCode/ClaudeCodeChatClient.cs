using System.Runtime.CompilerServices;
using System.Text;
using Claude.AgentSdk;
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

        // Build options
        var optionsBuilder = Claude.AgentSdk.Claude.Options();

        if (!string.IsNullOrEmpty(modelName))
        {
            optionsBuilder.Model(modelName);
        }

        if (!string.IsNullOrEmpty(configuration.SystemPrompt))
        {
            optionsBuilder.SystemPrompt(configuration.SystemPrompt);
        }

        if (configuration.MaxTurns.HasValue)
        {
            optionsBuilder.MaxTurns(configuration.MaxTurns.Value);
        }

        if (configuration.MaxBudgetUsd.HasValue)
        {
            optionsBuilder.MaxBudget(configuration.MaxBudgetUsd.Value);
        }

        if (!string.IsNullOrEmpty(configuration.CliPath))
        {
            optionsBuilder.CliPath(configuration.CliPath);
        }

        if (!string.IsNullOrEmpty(configuration.WorkingDirectory))
        {
            optionsBuilder.Cwd(configuration.WorkingDirectory);
        }

        var claudeOptions = optionsBuilder.Build();

        logger?.LogInformation("Starting Claude Code query with model {Model}", modelName);

        // Set up timeout
        using var timeoutCts = new CancellationTokenSource(configuration.SessionTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // Use the static QueryAsync method for streaming
        await foreach (var message in Claude.AgentSdk.Claude.QueryAsync(userPrompt, claudeOptions, cancellationToken: linkedCts.Token))
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

                                if (toolUseBlock.Input.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
                                    toolUseBlock.Input.ValueKind != System.Text.Json.JsonValueKind.Null)
                                {
                                    try
                                    {
                                        var inputJson = System.Text.Json.JsonSerializer.Serialize(toolUseBlock.Input);
                                        arguments = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(inputJson);
                                    }
                                    catch
                                    {
                                        // Ignore deserialization errors
                                    }
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
                        resultMessage.TotalCostUsd ?? 0m);
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
        if (message.Contents.Count == 0)
            return string.Empty;

        var textParts = message.Contents
            .OfType<TextContent>()
            .Select(c => c.Text);

        return string.Join("", textParts);
    }
}
