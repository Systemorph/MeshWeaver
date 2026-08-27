using System.ComponentModel;
using System.Reactive.Linq;
using System.Text;
using MeshWeaver.AI.Stores;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Gives an agent a durable working area — a scratchpad it can write notes, plans and intermediate
/// results into and read back later — stored as mesh nodes rather than as process-local state.
///
/// <para>The store is <see cref="MeshNodeAgentFileStore"/>, our implementation of the Microsoft Agent
/// Framework's <c>AgentFileStore</c> abstraction, rooted per thread at <c>{threadPath}/Files</c>. So
/// the files are ordinary content: versioned, access-controlled, visible in the portal, and readable
/// by every other mesh consumer — not a directory on a pod that disappears on the next deploy.</para>
///
/// <para><b>The tool-call architecture is unchanged.</b> This is an ordinary
/// <see cref="IAgentPlugin"/>-shaped plugin resolved by name from
/// <c>ChatClientAgentFactory.ResolvePluginTools</c>, exactly like <c>Version</c>,
/// <c>Collaboration</c> or <c>Lsp</c>. Agents opt in with <c>plugins: [AgentFiles]</c> in their
/// frontmatter. We do NOT route through MAF's harness loop or its own tool injection.</para>
///
/// <para>The tool methods return <see cref="Task{T}"/> because that is what
/// <see cref="AIFunctionFactory"/> requires; each is a one-line boundary adapter over the store's
/// reactive API, with no <c>async</c> and no work of its own.</para>
/// </summary>
/// <param name="hub">Hub supplying the workspace, I/O pool and the caller's identity.</param>
/// <param name="chat">The chat session, whose execution context roots the store at the thread.</param>
public sealed class AgentFilesPlugin(IMessageHub hub, IAgentChat chat) : IAgentPlugin
{
    /// <summary>The name agents declare in frontmatter — <c>AgentFiles</c>.</summary>
    public string Name => "AgentFiles";

    /// <summary>
    /// The store for the current thread, or <c>null</c> when the chat has no execution context (no
    /// thread yet). Built per call rather than cached so a session that moves between threads never
    /// writes into the previous thread's area.
    /// </summary>
    private MeshNodeAgentFileStore? Store =>
        chat.ExecutionContext?.ThreadPath is { Length: > 0 } threadPath
            ? new MeshNodeAgentFileStore(hub, $"{threadPath}/Files")
            : null;

    /// <inheritdoc />
    public IEnumerable<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create(ReadFile, name: "read_agent_file",
            description: "Reads one of your working files by its path within your working area, and "
                       + "returns its content. Returns a not-found message when the file does not exist."),
        AIFunctionFactory.Create(WriteFile, name: "write_agent_file",
            description: "Creates or overwrites one of your working files. Use this to persist notes, "
                       + "plans, drafts or intermediate results that should survive beyond this turn. "
                       + "Paths are relative to your working area and may contain folders (e.g. "
                       + "'research/findings.md')."),
        AIFunctionFactory.Create(ListFiles, name: "list_agent_files",
            description: "Lists the direct contents of a folder in your working area — subfolders "
                       + "first, then files. Pass an empty string for the top level."),
        AIFunctionFactory.Create(SearchFiles, name: "search_agent_files",
            description: "Searches the CONTENT of your working files with a regular expression and "
                       + "returns each matching file with its matching lines. Optionally restrict "
                       + "which files are searched with a glob such as '*.md' or '**/*.md'."),
        AIFunctionFactory.Create(DeleteFile, name: "delete_agent_file",
            description: "Deletes one of your working files."),
    ];

    // Boundary adapters. AIFunctionFactory requires Task<string>; the bodies are reactive and each
    // ends in the ONE shared bridge, ToolTask.Bridge — the store's own API is IObservable throughout.
    //
    // 🚨 These used to end in `.Catch(...).FirstAsync().ToTask(ct)`. The token was observed and the
    // fault was handled fluently, so the two loud terminals were covered — but the THIRD one was
    // not. A source that completes WITHOUT emitting reaches FirstAsync, which signals
    // InvalidOperationException("Sequence contains no elements") — past the .Catch, which sits
    // upstream of it — so the model got a stack trace where it needed an answer. And that terminal
    // is reachable here, not theoretical: MeshNodeStreamCache completes its per-path subjects on
    // eviction and on dispose, which is exactly what a thread whose hub is going away does.
    // ToolTask.Bridge makes the empty completion an ANSWER, and keeps the other three.

    private Task<string> ReadFile(
        [Description("Path of the file within your working area, e.g. 'notes.md' or 'research/findings.md'.")]
        string path,
        CancellationToken cancellationToken) =>
        Store is not { } store
            ? Task.FromResult(NoThread)
            : ToolTask.Bridge(
                store.Read(path).Select(content => content ?? $"No file at '{path}'."),
                cancellationToken,
                answer => answer,
                ex => $"Could not read '{path}': {ex.Message}",
                () => $"No file at '{path}'.");

    private Task<string> WriteFile(
        [Description("Path of the file within your working area, e.g. 'notes.md' or 'research/findings.md'.")]
        string path,
        [Description("The full content to store. Overwrites the file when it already exists.")]
        string content,
        CancellationToken cancellationToken) =>
        Store is not { } store
            ? Task.FromResult(NoThread)
            : ToolTask.Bridge(
                store.Write(path, content)
                    .Select(node => $"Saved '{path}' ({content.Length} characters) at {node.Path}."),
                cancellationToken,
                answer => answer,
                ex => $"Could not save '{path}': {ex.Message}",
                () => $"Could not save '{path}': the write produced no result.");

    private Task<string> ListFiles(
        [Description("Folder within your working area to list. Empty string lists the top level.")]
        string directory,
        CancellationToken cancellationToken) =>
        Store is not { } store
            ? Task.FromResult(NoThread)
            : ToolTask.Bridge(
                store.ListChildren(directory ?? "").Select(FormatListing),
                cancellationToken,
                answer => answer,
                ex => $"Could not list '{directory}': {ex.Message}",
                () => "(empty)");

    private Task<string> SearchFiles(
        [Description("Folder within your working area to search. Empty string searches from the top level.")]
        string directory,
        [Description("Regular expression matched case-insensitively against each line of file content.")]
        string pattern,
        [Description("Optional glob restricting which files are searched, e.g. '*.md' or '**/*.md'. Empty for all.")]
        string? glob,
        [Description("Whether to search all nested folders rather than only the direct contents.")]
        bool recursive,
        CancellationToken cancellationToken) =>
        Store is not { } store
            ? Task.FromResult(NoThread)
            : ToolTask.Bridge(
                store.Search(directory ?? "", pattern, string.IsNullOrWhiteSpace(glob) ? null : glob, recursive)
                    .Select(FormatMatches),
                cancellationToken,
                answer => answer,
                ex => $"Could not search '{directory}': {ex.Message}",
                () => "No matches.");

    private Task<string> DeleteFile(
        [Description("Path of the file within your working area to delete.")]
        string path,
        CancellationToken cancellationToken) =>
        Store is not { } store
            ? Task.FromResult(NoThread)
            : ToolTask.Bridge(
                store.Delete(path).Select(deleted => deleted ? $"Deleted '{path}'." : $"No file at '{path}'."),
                cancellationToken,
                answer => answer,
                ex => $"Could not delete '{path}': {ex.Message}",
                () => $"No file at '{path}'.");

    private const string NoThread =
        "No working area is available — this conversation has no thread yet. Try again after the "
        + "conversation has started.";

    private static string FormatListing(IReadOnlyCollection<FileStoreEntry> entries) =>
        entries.Count == 0
            ? "(empty)"
            : string.Join("\n", entries.Select(entry =>
                entry.Type == FileStoreEntry.Directory ? $"{entry.Name}/" : entry.Name));

    private static string FormatMatches(IReadOnlyCollection<FileSearchResult> results)
    {
        if (results.Count == 0)
            return "No matches.";

        var text = new StringBuilder();
        foreach (var result in results)
        {
            text.Append(result.FileName).Append('\n');
            foreach (var match in result.MatchingLines)
                text.Append("  ").Append(match.LineNumber).Append(": ").Append(match.Line).Append('\n');
        }
        return text.ToString();
    }
}
