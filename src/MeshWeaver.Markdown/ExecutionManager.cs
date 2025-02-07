using MeshWeaver.Data;
using MeshWeaver.Kernel;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown;


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

    public static void Execute(this ISynchronizationStream stream, Address address, IReadOnlyList<(string Id, string Code)> codeBlock)
    {
        var manager = stream.Get<ExecutionManager>();
        if(manager == null)
            stream.Set(manager = new(stream.Hub, address));

        manager.Update(codeBlock);
    }
}
