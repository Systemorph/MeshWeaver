using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GitHub.Copilot.SDK;
using MeshWeaver.AI.Connect;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Reactive;
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
    private readonly string? githubToken;
    // The user's selectable MeshWeaver agents — injected into the Copilot session's system message
    // (a guaranteed path; the SDK also discovers the workspace skills via the working dir). Resolved per session.
    private readonly IObservable<IReadOnlyList<AgentSkill>>? agentSkills;
    // Automatic MCP back-connection — the mesh is this CLI's workspace. Resolved per session to the
    // per-user `meshweaver` HTTP MCP server (Bearer-authenticated as the calling user).
    private readonly IMcpBackConnection? mcpBackConnection;
    private readonly string? userId;
    private readonly string? userName;
    private readonly string? userEmail;
    private readonly ILogger? logger;
    // Genuine IO (subprocess CLI spawn + SDK network round-trips) runs off the hub scheduler,
    // bounded, through the Http pool — the ControlledIoPooling boundary. Everything ABOVE the
    // pool is IObservable; the only async/await lives inside the leaves the pool owns.
    private readonly IIoPool ioPool;
    // The connect/start handshake is a one-shot resource: the promise-cache runs it at most once and
    // replays to every later caller (pool.Run is eager + ReplaySubject-backed). Built once via an
    // atomic ref swap (NOT a lock-for-async) — replaces the former SemaphoreSlim clientLock.
    private IObservable<CopilotClient>? connectPromise;
    private bool disposed;

    /// <summary>
    /// Creates a chat client that bridges the GitHub Copilot CLI/SDK to the Microsoft.Extensions.AI streaming contract.
    /// </summary>
    /// <param name="configuration">Copilot CLI configuration (CLI path/url, port).</param>
    /// <param name="modelName">Optional model id to report in <see cref="Metadata"/>; Copilot self-selects, so this is informational only.</param>
    /// <param name="logger">Optional logger for session diagnostics.</param>
    /// <param name="githubToken">The calling user's GitHub token; when null the CLI uses the machine's logged-in user (ambient auth).</param>
    /// <param name="ioPool">I/O pool that runs the subprocess spawn and SDK round-trips off the hub scheduler; defaults to an unbounded pool when null.</param>
    /// <param name="agentSkills">Stream of the user's selectable MeshWeaver agents, injected into the Copilot session's system message; resolved per session.</param>
    /// <param name="mcpBackConnection">Automatic MCP back-connection that exposes the mesh as this CLI's workspace.</param>
    /// <param name="userId">Calling user's id, forwarded for per-user context.</param>
    /// <param name="userName">Calling user's display name, forwarded for per-user context.</param>
    /// <param name="userEmail">Calling user's email, forwarded for per-user context.</param>
    public CopilotChatClient(
        CopilotConfiguration configuration,
        string? modelName = null,
        ILogger<CopilotChatClient>? logger = null,
        string? githubToken = null,
        IIoPool? ioPool = null,
        IObservable<IReadOnlyList<AgentSkill>>? agentSkills = null,
        IMcpBackConnection? mcpBackConnection = null,
        string? userId = null,
        string? userName = null,
        string? userEmail = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.modelName = modelName;
        this.logger = logger;
        // Per-user auth pass-through (co-hosted multi-user): the calling user's
        // GitHub token. When null, the CLI uses the machine's logged-in user
        // (single-user dev / ambient auth).
        this.githubToken = githubToken;
        this.ioPool = ioPool ?? IoPool.Unbounded;
        this.agentSkills = agentSkills;
        this.mcpBackConnection = mcpBackConnection;
        this.userId = userId;
        this.userName = userName;
        this.userEmail = userEmail;
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

        // M.E.AI contract surface: the only await/await-foreach the framework mandates. It consumes
        // the public streaming method, which is itself pure IObservable bridged to IAsyncEnumerable.
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
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        // IObservable up to the M.E.AI boundary; the framework IObservable→IAsyncEnumerable bridge
        // (ToAsyncEnumerableSequence) is the single sanctioned conversion — no hand-rolled .ToTask().
        => BuildResponseStream(messages.ToList(), options).ToAsyncEnumerableSequence(cancellationToken);

    /// <summary>
    /// The whole round as a cold <see cref="IObservable{T}"/>: the connect promise (pooled, replayed)
    /// composed into the session stream (the SDK IO leaf, run through <see cref="IIoPool.InvokeStream"/>).
    /// No <c>await</c>, no <c>.ToTask()</c> — the only async/await is sealed inside the pooled leaf.
    /// </summary>
    private IObservable<ChatResponseUpdate> BuildResponseStream(
        IReadOnlyList<ChatMessage> messages, ChatOptions? options)
        => GetOrCreateConnectPromise()
            .SelectMany(client => ioPool.InvokeStream(ct => StreamSessionAsync(client, messages, options, ct)));

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(IChatClient))
            return this;
        return null;
    }

    /// <inheritdoc />
    public void Dispose() => TeardownReactive();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Disposal is reactive (ControlledIoPooling → "Dispose() fires, the mesh drains"): kick off the
        // StopAsync teardown on the I/O pool and return immediately. A synchronous Dispose() must NOT
        // block on async work, and DisposeAsync must NOT bridge through .ToTask(); both fire-and-forget
        // the pooled stop so the subprocess is torn down while no caller (possibly a hub turn) parks.
        TeardownReactive();
        return ValueTask.CompletedTask;
    }

    private void TeardownReactive()
    {
        if (disposed)
            return;
        disposed = true;

        var promise = Interlocked.Exchange(ref connectPromise, null);
        if (promise is null)
            return;

        // StopAsync is the IO leaf -> Http pool. Subscribe (cold-observable side effect) so the work
        // actually runs; errors are logged, never thrown into a disposing scheduler.
        promise
            .SelectMany(client => ioPool.Invoke(_ => client.StopAsync()))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "Copilot client teardown failed"));
    }

    private IObservable<CopilotClient> GetOrCreateConnectPromise()
    {
        var promise = connectPromise;
        if (promise is not null)
            return promise;

        // pool.Run is eager (kicks the connect off on the Http pool NOW) + ReplaySubject-backed, so
        // concurrent first callers all observe the single connection. CompareExchange publishes only
        // if nobody else won the race; otherwise we drop ours and use theirs.
        var candidate = ioPool.Run(StartClientAsync);
        return Interlocked.CompareExchange(ref connectPromise, candidate, null) ?? candidate;
    }

    // ── SDK / subprocess boundary: the leaves the I/O pool owns — the ONLY place async/await lives ──

    private async Task<CopilotClient> StartClientAsync(CancellationToken cancellationToken)
    {
        try
        {
            var clientOptions = new CopilotClientOptions
            {
                AutoStart = true
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

            // Auth: a per-user GitHub token wins (co-hosted multi-user); otherwise
            // fall back to the machine's logged-in user (dev / ambient auth).
            if (!string.IsNullOrEmpty(githubToken))
                clientOptions.GitHubToken = githubToken;
            else
                clientOptions.UseLoggedInUser = true;

            var client = new CopilotClient(clientOptions);
            await client.StartAsync(cancellationToken).ConfigureAwait(false);

            logger?.LogInformation("Copilot client started successfully");

            return client;
        }
        catch (Exception ex)
        {
            // Not logged in / token rejected → actionable "/login" affordance (ThreadExecution renders
            // it), not the generic "CLI not installed" message.
            var msg = ex.Message ?? string.Empty;
            if (msg.Contains("auth", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("login", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("unauthor", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("credential", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("not signed in", StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogWarning(ex, "Copilot not authenticated for this user");
                throw new AuthRequiredException(
                    Harnesses.Copilot,
                    "Not logged in to GitHub Copilot. Run /login to connect.", ex);
            }
            logger?.LogError(ex, "Failed to start Copilot client. Ensure the Copilot CLI is installed and available in PATH.");
            throw new InvalidOperationException(
                "Failed to start Copilot client. Ensure the Copilot CLI is installed and available in PATH. " +
                "See: https://docs.github.com/en/copilot/managing-copilot/configure-personal-settings/installing-the-github-copilot-in-the-cli", ex);
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> StreamSessionAsync(
        CopilotClient client,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();

        // Resolve the user's selectable MeshWeaver agents (best-effort) → system message, AND the
        // per-user `meshweaver` HTTP MCP server → the session, so Copilot reaches the mesh by default.
        var agentsSection = await ResolveAgentsSectionAsync(cancellationToken);
        var mcpServers = await ResolveMcpServersAsync(cancellationToken);

        // Build session configuration, including system messages as SystemMessage
        var sessionConfig = BuildSessionConfig(options, messageList, agentsSection, mcpServers);
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

    private SessionConfig BuildSessionConfig(
        ChatOptions? options, List<ChatMessage>? messages = null, string? agentsSection = null,
        IDictionary<string, McpServerConfig>? mcpServers = null)
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

        // Extract system messages (agent instructions from ChatClientAgent) + the projected MeshWeaver
        // agents section, and set them as the session SystemMessage.
        var systemParts = messages?
            .Where(m => m.Role == ChatRole.System)
            .Select(GetTextContent)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList() ?? new List<string>();
        // When the mesh MCP server is wired, tell the agent it's there (guaranteed, regardless of file discovery).
        if (mcpServers is { Count: > 0 })
            systemParts.Insert(0,
                "The memex mesh is available through the `meshweaver` MCP server (wired automatically, " +
                "authenticated as you). The mesh — not a local file tree — is your workspace: use the MCP " +
                "tools to read and modify content.");
        if (!string.IsNullOrWhiteSpace(agentsSection))
            systemParts.Add(agentsSection!);

        if (systemParts.Count > 0)
        {
            config.SystemMessage = new SystemMessageConfig
            {
                Content = string.Join("\n\n", systemParts),
                Mode = SystemMessageMode.Append
            };
        }

        // Add tools if provided - the SDK accepts AIFunction from Microsoft.Extensions.AI.Abstractions
        if (options?.Tools != null && options.Tools.Count > 0)
        {
            config.Tools = options.Tools.OfType<AIFunction>().ToList();
        }

        // The mesh: per-user `meshweaver` HTTP MCP server (Bearer-authenticated as the calling user).
        if (mcpServers is { Count: > 0 })
            config.McpServers = mcpServers;

        // Point the session at the shared sync workspace (.claude/skills + AGENTS.md) and let the CLI
        // discover its skills + instructions from the working directory.
        if (!string.IsNullOrEmpty(configuration.SkillsDirectory))
        {
            config.WorkingDirectory = configuration.SkillsDirectory;
            config.EnableConfigDiscovery = true;
        }

        // Headless: auto-approve tool/permission prompts so MCP + tool calls proceed without a TTY.
        // (MCP tools are still scoped to the user's own permissions via the Bearer token.)
        config.OnPermissionRequest = PermissionHandler.ApproveAll;

        return config;
    }

    /// <summary>
    /// Resolves the per-user <c>meshweaver</c> HTTP MCP server (Bearer-authenticated as the calling
    /// user) for the session, or null when unavailable. The mesh is this CLI's workspace. The await is
    /// at the SDK boundary (inside the IIoPool stream leaf), never on a hub scheduler.
    /// </summary>
    private async Task<IDictionary<string, McpServerConfig>?> ResolveMcpServersAsync(CancellationToken cancellationToken)
    {
        if (mcpBackConnection is null || string.IsNullOrEmpty(userId))
            return null;
        try
        {
            var info = await mcpBackConnection.EnsureForUser(userId, userName, userEmail)
                .FirstOrDefaultAsync().ToTask(cancellationToken);
            if (info is null)
                return null;
            logger?.LogInformation("Copilot MCP workspace wired to {McpUrl}", info.McpUrl);
            return new Dictionary<string, McpServerConfig>
            {
                ["meshweaver"] = new McpHttpServerConfig
                {
                    Url = info.McpUrl,
                    Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {info.BearerToken}" },
                    Tools = new List<string> { "*" },
                }
            };
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not provision the Copilot MCP back-connection; running without mesh access.");
            return null;
        }
    }

    /// <summary>
    /// Resolves the projected MeshWeaver agents (best-effort, bounded) into a system-message section,
    /// or null when none / unavailable. The await is at the SDK boundary (inside the IIoPool stream
    /// leaf), never on a hub scheduler.
    /// </summary>
    private async Task<string?> ResolveAgentsSectionAsync(CancellationToken cancellationToken)
    {
        if (agentSkills is null)
            return null;
        try
        {
            var skills = await agentSkills
                .Take(1).Timeout(TimeSpan.FromSeconds(5))
                .FirstOrDefaultAsync().ToTask(cancellationToken);
            return BuildAgentsSection(skills);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not project MeshWeaver agents for the Copilot session.");
            return null;
        }
    }

    /// <summary>Renders the selectable MeshWeaver agents as a markdown system-message section.</summary>
    private static string? BuildAgentsSection(IReadOnlyList<AgentSkill>? skills)
    {
        if (skills is null || skills.Count == 0)
            return null;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Available MeshWeaver agents");
        sb.AppendLine("Adopt the behavior of one of these MeshWeaver agents when the user's request matches it. Each has a name, a description, and instructions:");
        foreach (var s in skills)
        {
            if (string.IsNullOrWhiteSpace(s.Instructions))
                continue;
            sb.AppendLine();
            sb.AppendLine($"## {s.Name}");
            if (!string.IsNullOrWhiteSpace(s.Description))
                sb.AppendLine(s.Description);
            sb.AppendLine();
            sb.AppendLine(s.Instructions);
        }
        return sb.ToString();
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
