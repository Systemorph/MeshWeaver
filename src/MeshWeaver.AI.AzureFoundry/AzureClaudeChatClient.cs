using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// IChatClient implementation for Claude models deployed on Azure AI Foundry.
/// Uses the Anthropic Messages API format with Azure endpoints.
/// </summary>
public class AzureClaudeChatClient : IChatClient
{
    private const string AnthropicVersion = "2023-06-01";
    private const int DefaultMaxTokens = 4096;

    private readonly HttpClient httpClient;
    private readonly string endpoint;
    private readonly string apiKey;
    private readonly string modelId;
    private readonly ILogger? logger;
    private readonly bool ownsHttpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Creates a new Azure Claude chat client.
    /// </summary>
    /// <param name="endpoint">The Azure AI Foundry endpoint (e.g., https://resource.services.ai.azure.com/anthropic/v1/messages)</param>
    /// <param name="apiKey">The Azure API key</param>
    /// <param name="modelId">The Claude model ID (e.g., claude-sonnet-4-20250514)</param>
    /// <param name="httpClient">Optional custom HttpClient</param>
    /// <param name="logger">Optional logger</param>
    public AzureClaudeChatClient(
        string endpoint,
        string apiKey,
        string modelId,
        HttpClient? httpClient = null,
        ILogger<AzureClaudeChatClient>? logger = null)
    {
        if (string.IsNullOrEmpty(endpoint))
            throw new ArgumentException("Endpoint is required", nameof(endpoint));
        if (string.IsNullOrEmpty(apiKey))
            throw new ArgumentException("API key is required", nameof(apiKey));
        if (string.IsNullOrEmpty(modelId))
            throw new ArgumentException("Model ID is required", nameof(modelId));

        // Ensure endpoint ends with /messages
        this.endpoint = endpoint.TrimEnd('/');
        if (!this.endpoint.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
        {
            if (this.endpoint.EndsWith("/anthropic", StringComparison.OrdinalIgnoreCase))
                this.endpoint += "/v1/messages";
            else if (!this.endpoint.Contains("/anthropic/"))
                this.endpoint += "/anthropic/v1/messages";
        }

        this.apiKey = apiKey;
        this.modelId = modelId;
        this.logger = logger;
        this.ownsHttpClient = httpClient == null;
        this.httpClient = httpClient ?? new HttpClient();
    }

    /// <inheritdoc />
    public ChatClientMetadata Metadata => new("AzureClaudeChatClient", new Uri(endpoint), modelId);

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(messages, options, stream: false);
        var response = await SendRequestAsync(request, cancellationToken);
        return ConvertToResponse(response);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(messages, options, stream: true);
        var httpRequest = CreateHttpRequest(request);

        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var contentBuilder = new StringBuilder();
        string? currentRole = null;
        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                continue;

            var data = line[6..];
            if (data == "[DONE]")
                break;

            ClaudeStreamEvent? streamEvent;
            try
            {
                streamEvent = JsonSerializer.Deserialize<ClaudeStreamEvent>(data, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (streamEvent == null)
                continue;

            switch (streamEvent.Type)
            {
                case "message_start":
                    currentRole = streamEvent.Message?.Role ?? "assistant";
                    break;

                case "content_block_delta":
                    if (streamEvent.Delta?.Text != null)
                    {
                        contentBuilder.Append(streamEvent.Delta.Text);
                        yield return new ChatResponseUpdate(ChatRole.Assistant, streamEvent.Delta.Text);
                    }
                    break;

                case "message_delta":
                    if (streamEvent.Delta?.StopReason != null)
                    {
                        yield return new ChatResponseUpdate(ChatRole.Assistant, string.Empty)
                        {
                            FinishReason = ConvertStopReason(streamEvent.Delta.StopReason)
                        };
                    }
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
        if (ownsHttpClient)
            httpClient.Dispose();
    }

    private ClaudeRequest BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var messageList = messages.ToList();
        string? systemPrompt = null;
        var claudeMessages = new List<ClaudeMessage>();

        foreach (var message in messageList)
        {
            var role = message.Role.Value.ToLowerInvariant();

            if (role == "system")
            {
                systemPrompt = GetTextContent(message);
                continue;
            }

            // Map user/assistant roles
            var claudeRole = role == "user" ? "user" : "assistant";
            var content = GetTextContent(message);

            if (!string.IsNullOrEmpty(content))
            {
                claudeMessages.Add(new ClaudeMessage
                {
                    Role = claudeRole,
                    Content = content
                });
            }
        }

        return new ClaudeRequest
        {
            Model = modelId,
            Messages = claudeMessages,
            System = systemPrompt,
            MaxTokens = options?.MaxOutputTokens ?? DefaultMaxTokens,
            Temperature = options?.Temperature,
            TopP = options?.TopP,
            Stream = stream
        };
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

    private HttpRequestMessage CreateHttpRequest(ClaudeRequest request)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        logger?.LogDebug("Sending request to Azure Claude: {Json}", json);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        httpRequest.Headers.Add("x-api-key", apiKey);
        httpRequest.Headers.Add("anthropic-version", AnthropicVersion);

        return httpRequest;
    }

    private async Task<ClaudeResponse> SendRequestAsync(ClaudeRequest request, CancellationToken cancellationToken)
    {
        using var httpRequest = CreateHttpRequest(request);
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger?.LogError("Azure Claude API error: {StatusCode} - {Body}", response.StatusCode, responseBody);
            throw new HttpRequestException($"Azure Claude API error: {response.StatusCode} - {responseBody}");
        }

        logger?.LogDebug("Received response from Azure Claude: {Json}", responseBody);

        return JsonSerializer.Deserialize<ClaudeResponse>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize Claude response");
    }

    private static ChatResponse ConvertToResponse(ClaudeResponse response)
    {
        var content = response.Content?.FirstOrDefault(c => c.Type == "text")?.Text ?? string.Empty;

        var chatMessage = new ChatMessage(ChatRole.Assistant, content);

        return new ChatResponse(chatMessage)
        {
            FinishReason = ConvertStopReason(response.StopReason),
            ModelId = response.Model,
            Usage = response.Usage != null
                ? new UsageDetails
                {
                    InputTokenCount = response.Usage.InputTokens,
                    OutputTokenCount = response.Usage.OutputTokens
                }
                : null
        };
    }

    private static ChatFinishReason? ConvertStopReason(string? stopReason)
    {
        return stopReason switch
        {
            "end_turn" => ChatFinishReason.Stop,
            "max_tokens" => ChatFinishReason.Length,
            "stop_sequence" => ChatFinishReason.Stop,
            "tool_use" => ChatFinishReason.ToolCalls,
            _ => null
        };
    }

    #region Request/Response Models

    private class ClaudeRequest
    {
        public string Model { get; set; } = null!;
        public List<ClaudeMessage> Messages { get; set; } = new();
        public string? System { get; set; }
        public int MaxTokens { get; set; }
        public float? Temperature { get; set; }
        public float? TopP { get; set; }
        public bool Stream { get; set; }
    }

    private class ClaudeMessage
    {
        public string Role { get; set; } = null!;
        public string Content { get; set; } = null!;
    }

    private class ClaudeResponse
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Role { get; set; }
        public List<ClaudeContent>? Content { get; set; }
        public string? Model { get; set; }
        public string? StopReason { get; set; }
        public ClaudeUsage? Usage { get; set; }
    }

    private class ClaudeContent
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
    }

    private class ClaudeUsage
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }

    private class ClaudeStreamEvent
    {
        public string? Type { get; set; }
        public ClaudeStreamMessage? Message { get; set; }
        public ClaudeStreamDelta? Delta { get; set; }
        public int? Index { get; set; }
    }

    private class ClaudeStreamMessage
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Role { get; set; }
        public string? Model { get; set; }
    }

    private class ClaudeStreamDelta
    {
        public string? Type { get; set; }
        public string? Text { get; set; }
        public string? StopReason { get; set; }
    }

    #endregion
}
