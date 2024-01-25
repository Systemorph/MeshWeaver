using System.Threading.Tasks.Dataflow;

namespace Systemorph.Queues;

public record QueueOptions(ExecutionDataflowBlockOptions ExecutionDataflowBlockOptions, int DebounceOnEnqueue = 0);
public record QueueDependency(object Address);