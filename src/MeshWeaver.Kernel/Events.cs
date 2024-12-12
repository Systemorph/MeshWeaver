namespace MeshWeaver.Kernel;

public record MeshWeaverKernelEvent(string Event);
public record MeshWeaverKernelCommand(string Command);

public record SubscribeKernelEventsRequest;
public record UnsubscribeKernelEventsRequest;
