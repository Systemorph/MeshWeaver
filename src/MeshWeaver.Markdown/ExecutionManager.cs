using MeshWeaver.Data;
using MeshWeaver.Kernel;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown;


/// <summary>
/// Extensions for submitting a markdown document's executable code blocks to a kernel, tracking which
/// blocks have already run so only changed/new blocks are re-submitted.
/// </summary>
public static class MarkdownExecutionExtensions
{
    internal class ExecutionManager(IMessageHub hub, Address address)
    {
        private IReadOnlyList<(string Id, string Code)> executed = [];

        public void Update(IReadOnlyList<(string Id, string Code)> codeBlocks)
        {
            var i = 0;
            while (executed.Count > i && codeBlocks.Count > i && Equals(executed[i], codeBlocks[i]))
                ++i;

            executed = executed.Skip(i).Concat(codeBlocks.Skip(i).Select(ExecuteAndReturn)).ToArray();
        }

        private (string Id, string Code) ExecuteAndReturn((string Id, string Code) tuple)
        {
            hub.Post(new SubmitCodeRequest(tuple.Code) { Id = tuple.Id }, o => o.WithTarget(address));
            return tuple;
        }
    }

    /// <summary>
    /// Submits the given code blocks to <paramref name="address"/> via a per-stream execution manager,
    /// re-running only the blocks that changed since the previous call (matched by leading common prefix).
    /// </summary>
    /// <param name="stream">The synchronization stream that owns (and caches) the execution manager state.</param>
    /// <param name="address">The kernel address that receives the <c>SubmitCodeRequest</c> messages.</param>
    /// <param name="codeBlock">The ordered list of (id, code) blocks for the current document state.</param>
    public static void Execute(this ISynchronizationStream stream, Address address, IReadOnlyList<(string Id, string Code)> codeBlock)
    {
        var manager = stream.Get<ExecutionManager>();
        if(manager == null)
            stream.Set(manager = new(stream.Hub, address));

        manager.Update(codeBlock);
    }
}
