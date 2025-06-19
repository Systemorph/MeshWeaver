using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;

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
