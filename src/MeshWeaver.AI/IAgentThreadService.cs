using System.Text.Json;
using Microsoft.Agents.AI;

namespace MeshWeaver.AI;

/// <summary>
/// Service for managing agent thread persistence
/// </summary>
public interface IAgentThreadService
{
    /// <summary>
    /// Saves a serialized agent thread
    /// </summary>
    /// <param name="threadId">The thread identifier</param>
    /// <param name="agentName">The agent name</param>
    /// <param name="serializedThread">The serialized thread data</param>
    Task SaveThreadAsync(string threadId, string agentName, JsonElement serializedThread);

    /// <summary>
    /// Loads a serialized agent thread
    /// </summary>
    /// <param name="threadId">The thread identifier</param>
    /// <param name="agentName">The agent name</param>
    /// <returns>The serialized thread data, or null if not found</returns>
    Task<JsonElement?> LoadThreadAsync(string threadId, string agentName);
}
