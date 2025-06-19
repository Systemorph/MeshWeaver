namespace MeshWeaver.AI;

/// <summary>
/// Interface for agent definitions that require file uploads
/// </summary>
public interface IAgentDefinitionWithFiles : IAgentDefinition
{
    /// <summary>
    /// Gets the files that should be uploaded to the agent
    /// </summary>
    /// <returns>A collection of file information containing file paths and descriptions</returns>
    IAsyncEnumerable<AgentFileInfo> GetFilesAsync();
}

/// <summary>
/// Information about a file to be uploaded to an agent
/// </summary>
public record AgentFileInfo(Stream Stream, string FileName);
