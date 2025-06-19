using Microsoft.Extensions.AI;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// Service for managing chat operations with AI
/// </summary>
public interface IChatService
{
    /// <summary>
    /// Gets the system prompt for chat conversations
    /// </summary>
    string SystemPrompt { get; }

    /// <summary>
    /// Gets an AI chat client
    /// </summary>
    IChatClient Get();

    /// <summary>
    /// Gets chat options for the specified context
    /// </summary>
    ChatOptions GetOptions(IMessageHub hub, string path);

    /// <summary>
    /// Gets progress message for function calls
    /// </summary>
    ProgressMessage GetProgressMessage(object functionCall);
}

/// <summary>
/// Represents a progress message for AI operations
/// </summary>
public class ProgressMessage
{
    public string Icon { get; set; } = "⏳";
    public string Message { get; set; } = "";
    public int Progress { get; set; } = 0;
}
