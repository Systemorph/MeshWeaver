using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Copilot;

/// <summary>
/// IChatClient implementation that bridges the GitHub Copilot SDK's event-based pattern
/// to the async enumerable streaming pattern required by Microsoft.Extensions.AI.
/// </summary>
public class CopilotChatClient : IChatClient, IAsyncDisposable
{
    private readonly CopilotConfiguration configuration;
    private readonly string? modelName;
    private readonly ILogger? logger;
    private CopilotClient? copilotClient;
    private bool disposed;
    private readonly SemaphoreSlim clientLock = new(1, 1);

    public CopilotChatClient(
        CopilotConfiguration configuration,
        string? modelName = null,
        ILogger<CopilotChatClient>? logger = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.modelName = modelName;
        this.logger = logger;
    }

    /// <inheritdoc />
    public ChatClientMetadata Metadata => new("GitHubCopilotChatClient", providerUri: null, modelName);

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var contents = new List<AIContent>();
        var allText = new System.Text.StringBuilder();

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
        var client = await GetOrCreateClientAsync(cancellationToken);

        // Build session configuration
        var sessionConfig = BuildSessionConfig(options);

        // Get the user prompt (last user message) and history
        var messageList = messages.ToList();
        var lastUserMessage = messageList.LastOrDefault(m => m.Role == ChatRole.User);
        var userPrompt = lastUserMessage != null ? GetTextContent(lastUserMessage) : string.Empty;

        var channel = Channel.CreateUnbounded<ChatResponseUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        Exception? sessionError = null;
        IDisposable? subscription = null;

        await using var session = await client.CreateSessionAsync(sessionConfig, cancellationToken);

        // Set up event handling using pattern matching
        subscription = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    var deltaContent = delta.Data?.DeltaContent;
                    if (!string.IsNullOrEmpty(deltaContent))
                    {
                        channel.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, deltaContent));
                    }
                    break;

                case AssistantMessageEvent msg:
                    var content = msg.Data?.Content;
                    if (!string.IsNullOrEmpty(content))
                    {
                        channel.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, content));
                    }
                    break;

                case ToolExecutionStartEvent toolStart:
                    var toolName = toolStart.Data?.ToolName;
                    if (!string.IsNullOrEmpty(toolName))
                    {
                        // Generate a unique ID for this tool call
                        var toolId = Guid.NewGuid().ToString();
                        IDictionary<string, object?>? arguments = null;

                        if (toolStart.Data?.Arguments != null)
                        {
                            try
                            {
                                var argsJson = System.Text.Json.JsonSerializer.Serialize(toolStart.Data.Arguments);
                                arguments = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson);
                            }
                            catch
                            {
                                // Ignore deserialization errors
                            }
                        }

                        var functionCall = new FunctionCallContent(toolId, toolName, arguments);
                        channel.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, [functionCall]));
                    }
                    break;

                case SessionIdleEvent:
                    channel.Writer.TryComplete();
                    break;

                case SessionErrorEvent error:
                    var errorMessage = error.Data?.Message ?? "Unknown session error";
                    sessionError = new InvalidOperationException(errorMessage);
                    logger?.LogError("Copilot session error: {Error}", errorMessage);
                    channel.Writer.TryComplete(sessionError);
                    break;

                case UserMessageEvent:
                case ToolExecutionCompleteEvent:
                case SessionStartEvent:
                    // These events don't produce output
                    break;
            }
        });

        try
        {
            // Send the user prompt to start the conversation
            await session.SendAsync(new MessageOptions
            {
                Prompt = userPrompt
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to send message to Copilot session");
            channel.Writer.TryComplete(ex);
        }

        // Set up timeout
        using var timeoutCts = new CancellationTokenSource(configuration.SessionTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            // Yield updates from the channel
            await foreach (var update in channel.Reader.ReadAllAsync(linkedCts.Token))
            {
                yield return update;
            }
        }
        finally
        {
            subscription?.Dispose();
        }

        // If there was an error, throw it
        if (sessionError != null)
        {
            throw sessionError;
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
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        disposed = true;

        await clientLock.WaitAsync();
        try
        {
            if (copilotClient != null)
            {
                await copilotClient.StopAsync();
                copilotClient = null;
            }
        }
        finally
        {
            clientLock.Release();
        }

        clientLock.Dispose();
    }

    private async Task<CopilotClient> GetOrCreateClientAsync(CancellationToken cancellationToken)
    {
        if (copilotClient != null)
            return copilotClient;

        await clientLock.WaitAsync(cancellationToken);
        try
        {
            if (copilotClient != null)
                return copilotClient;

            var clientOptions = new CopilotClientOptions
            {
                AutoStart = true,
                AutoRestart = true
            };

            if (!string.IsNullOrEmpty(configuration.CliPath))
            {
                clientOptions.CliPath = configuration.CliPath;
            }

            if (!string.IsNullOrEmpty(configuration.CliUrl))
            {
                clientOptions.CliUrl = configuration.CliUrl;
            }

            if (configuration.Port.HasValue)
            {
                clientOptions.Port = configuration.Port.Value;
            }

            copilotClient = new CopilotClient(clientOptions);
            await copilotClient.StartAsync(cancellationToken);

            logger?.LogInformation("Copilot client started successfully");

            return copilotClient;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to start Copilot client. Ensure the Copilot CLI is installed and available in PATH.");
            throw new InvalidOperationException(
                "Failed to start Copilot client. Ensure the Copilot CLI is installed and available in PATH. " +
                "See: https://docs.github.com/en/copilot/managing-copilot/configure-personal-settings/installing-the-github-copilot-in-the-cli", ex);
        }
        finally
        {
            clientLock.Release();
        }
    }

    private SessionConfig BuildSessionConfig(ChatOptions? options)
    {
        var config = new SessionConfig
        {
            Streaming = configuration.EnableStreaming
        };

        // Set model if specified
        if (!string.IsNullOrEmpty(modelName))
        {
            config.Model = modelName;
        }

        // Add tools if provided - the SDK accepts AIFunction from Microsoft.Extensions.AI.Abstractions
        if (options?.Tools != null && options.Tools.Count > 0)
        {
            config.Tools = options.Tools.OfType<AIFunction>().ToList();
        }

        return config;
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
