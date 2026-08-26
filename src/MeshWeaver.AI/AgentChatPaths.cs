
namespace MeshWeaver.AI;

/// <summary>
/// The agent-chat half of path resolution: turns a tool's user-supplied path into an absolute
/// mesh path using the chat's own context chip.
///
/// <para>The arithmetic itself is platform (<see cref="MeshOperations.ResolveContextPath"/>) and
/// takes a plain context string. This class is the ONE place that reads the context off an
/// <see cref="IAgentChat"/>, which is what keeps <c>MeshOperations</c> free of any agent type —
/// see issue #2276.</para>
/// </summary>
public static class AgentChatPaths
{
    /// <summary>
    /// Resolves <paramref name="path"/> against <paramref name="chat"/>'s context path.
    /// </summary>
    /// <param name="chat">The chat whose context chip supplies the base path.</param>
    /// <param name="path">The user-supplied path — absolute, relative, or a bare node name.</param>
    public static string ResolveContextPath(IAgentChat chat, string path)
        => MeshOperations.ResolveContextPath(chat.Context?.Context, path);
}
